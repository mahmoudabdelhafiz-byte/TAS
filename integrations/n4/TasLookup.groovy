/*
 * TasLookup — N4 Groovy code extension used by TAS (Truck Appointment System).
 *
 * Installed in N4 as a code extension named "TasLookup" (Administration > System > Code Extensions,
 * type GROOVY_WS_CODE_EXTENSION / "Groovy plug-in" depending on the N4 version) and called through
 * ArgoService.genericInvoke:
 *
 *   <groovy class-location="code-extension" class-name="TasLookup">
 *     <parameters><parameter id="request" value='{"units":[...],"orders":[...],"bls":[...]}'/></parameters>
 *   </groovy>
 *
 * READ-ONLY: it only looks things up and returns JSON (shape below). It never changes N4 data.
 *
 * The N4 API differs between versions. Lines marked "VERIFY" use calls whose names should be checked on your
 * N4 version (test system first). Everything else is plain Groovy. If a lookup throws, the whole call returns
 * {"error": "..."} and TAS treats N4 as unavailable (booking gets a warning, the re-check retries later).
 *
 * Answer:
 * {
 *   "units":  [{ "containerNo","found","gkey","category","freightKind","transitState","lineOperator","vgmKg","holds":[...] }],
 *   "orders": [{ "nbr","found","gkey","subType","lineOperator","quantityRemaining","cargoCutoff","holds":[...] }],
 *   "bls":    [{ "nbr","found","gkey","released","holds":[...] }]
 * }
 *   category:      IMPRT | EXPRT | STRGE | TRSHP | THRGH
 *   transitState:  S10_ADVISED | S20_INBOUND | S30_ECIN | S40_YARD | S50_ECOUT | S60_LOADED | S70_DEPARTED | S99_RETIRED
 *   holds:         ids of ACTIVE holds that stop road delivery / receipt (TAS maps them via N4:HoldMapping)
 *   cargoCutoff:   ISO-8601 with offset, e.g. "2026-10-21T18:00:00+03:00"
 */

import com.navis.argo.ContextHelper
import com.navis.argo.business.reference.Equipment
import com.navis.framework.business.Roastery
import com.navis.framework.portal.QueryUtils
import com.navis.framework.portal.query.DomainQuery
import com.navis.framework.portal.query.PredicateFactory
import com.navis.framework.persistence.HibernateApi
import com.navis.inventory.business.api.UnitFinder
import com.navis.inventory.business.units.Unit
import com.navis.inventory.business.units.UnitFacilityVisit
import groovy.json.JsonOutput
import groovy.json.JsonSlurper

import java.text.SimpleDateFormat

class TasLookup {

    private static final int MAX_ITEMS = 50

    public String execute(Map inParameters) {
        try {
            def request = new JsonSlurper().parseText(inParameters.get("request") as String)
            def units = (request.units ?: []).take(MAX_ITEMS)
            def orders = (request.orders ?: []).take(MAX_ITEMS)
            def bls = (request.bls ?: []).take(MAX_ITEMS)

            UnitFinder finder = (UnitFinder) Roastery.getBean(UnitFinder.BEAN_ID)
            return JsonOutput.toJson([
                units : units.collect { String nbr -> unitInfo(finder, nbr) },
                orders: orders.collect { String nbr -> orderInfo(nbr) },
                bls   : bls.collect { String nbr -> blInfo(nbr) },
            ])
        } catch (Throwable t) {
            return JsonOutput.toJson([error: (t.getClass().getSimpleName() + ": " + t.getMessage())])
        }
    }

    // ------------------------------------------------------------------ units

    private Map unitInfo(UnitFinder finder, String nbr) {
        Equipment equipment = Equipment.findEquipment(nbr)
        Unit unit = equipment == null ? null : finder.findActiveUnit(ContextHelper.getThreadComplex(), equipment)
        if (unit == null)
            return [containerNo: nbr, found: false]

        UnitFacilityVisit ufv = unit.getUnitActiveUfvNowActive()
        return [
            containerNo : nbr,
            found       : true,
            gkey        : String.valueOf(unit.getUnitGkey()),
            category    : unit.getUnitCategory()?.getKey(),
            freightKind : unit.getUnitFreightKind()?.getKey(),
            transitState: ufv?.getUfvTransitState()?.getKey(),
            lineOperator: unit.getUnitLineOperator()?.getBzuId(),
            vgmKg       : unit.getUnitGoodsAndCtrWtKgVerfiedGross(),        // VERIFY: VGM field name on your version
            holds       : activeRoadHolds(unit),
        ]
    }

    /** Active holds that block road delivery / receipt. */
    private List<String> activeRoadHolds(Unit unit) {
        // VERIFY: N4 keeps the active road impediments as text on the unit (comma-separated hold ids).
        // If your version exposes them differently (e.g. through ServicesManager flags), return the
        // list of ACTIVE hold ids here instead.
        String road = unit.getUnitImpedimentRoad()
        return road ? road.split(",").collect { it.trim() }.findAll { it } : []
    }

    // ------------------------------------------------------------------ equipment orders (bookings, EDOs)

    private Map orderInfo(String nbr) {
        // VERIFY: entity / field names of equipment orders on your version.
        DomainQuery dq = QueryUtils.createDomainQuery("EquipmentOrder")
            .addDqPredicate(PredicateFactory.eq("eqboNbr", nbr))
        List found = HibernateApi.getInstance().findEntitiesByDomainQuery(dq)
        if (found == null || found.isEmpty())
            return [nbr: nbr, found: false]
        if (found.size() > 1)                                                // same number for several lines
            return [nbr: nbr, found: true, quantityRemaining: null, holds: ["AMBIGUOUS_ORDER_NUMBER"]]

        def order = found.get(0)
        Integer remaining = null
        try {
            // VERIFY: quantity ordered minus tally received / delivered, summed over the order items
            remaining = order.getEqboOrderItems()?.sum { item -> (item.getEqoiQty() ?: 0) - (item.getEqoiTally() ?: 0) } as Integer
        } catch (MissingMethodException ignored) { }

        String cutoff = null
        try {
            // VERIFY: cargo cut-off of the order's vessel visit
            Date cargoCutoff = order.getEqoVesselVisit()?.getCvCvd()?.getCvdCargoCutoff()
            cutoff = iso(cargoCutoff)
        } catch (MissingMethodException ignored) { }

        return [
            nbr              : nbr,
            found            : true,
            gkey             : String.valueOf(order.getEqboGkey()),
            subType          : order.getEqboSubType()?.getKey(),
            lineOperator     : order.getEqoLine()?.getBzuId(),
            quantityRemaining: remaining,
            cargoCutoff      : cutoff,
            holds            : [],                                       // add order-level holds if your terminal uses them
        ]
    }

    // ------------------------------------------------------------------ bills of lading (breakbulk / bulk)

    private Map blInfo(String nbr) {
        // VERIFY: entity / field names of bills of lading on your version.
        DomainQuery dq = QueryUtils.createDomainQuery("BillOfLading")
            .addDqPredicate(PredicateFactory.eq("blNbr", nbr))
        List found = HibernateApi.getInstance().findEntitiesByDomainQuery(dq)
        if (found == null || found.isEmpty())
            return [nbr: nbr, found: false]

        def bl = found.get(0)
        List<String> holds = []
        try {
            // VERIFY: how your terminal records customs / release status on the BL
            String inbond = bl.getBlInbond()?.getKey()                     // e.g. INBOND / CANCEL
            if ("INBOND".equalsIgnoreCase(inbond)) holds << "CUSTOMS_INBOND"
        } catch (MissingMethodException ignored) { }

        return [
            nbr     : nbr,
            found   : true,
            gkey    : String.valueOf(bl.getBlGkey()),
            released: holds.isEmpty(),
            holds   : holds,
        ]
    }

    // ------------------------------------------------------------------ helpers

    private static String iso(Date date) {
        if (date == null) return null
        def format = new SimpleDateFormat("yyyy-MM-dd'T'HH:mm:ssXXX")
        format.setTimeZone(TimeZone.getDefault())                         // N4 server time zone
        return format.format(date)
    }
}
