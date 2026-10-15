# TAS ↔ Navis N4

TAS checks every booked move against N4 before the truck sets off — at booking, on the scheduled re-checks,
at trip start and at the gate. The integration is **read-only**: TAS never changes N4 data.

```
TAS ──SOAP──▶ ArgoService.genericInvoke ──▶ TasLookup (Groovy code extension in N4) ──▶ JSON
                                                                                         │
TAS ◀── checks (availability, holds, booking, cut-off, VGM, cargo release) ◀── rules ◀──┘
```

## 1. Install the code extension in N4

1. Take `TasLookup.groovy` from this folder.
2. **Check the lines marked `VERIFY`** on your N4 version (test system first): VGM field, road holds,
   equipment-order and bill-of-lading fields. Everything else is plain Groovy.
3. In N4: *Administration › System › Code Extensions* → add **TasLookup** with the file's content
   (type for web-service Groovy plug-ins on your version).
4. Test it directly with SoapUI against `…/apex/services/argoservice`, operation `genericInvoke`:

```xml
<xmlDoc><![CDATA[<groovy class-location="code-extension" class-name="TasLookup">
  <parameters><parameter id="request" value='{"units":["MSCU1234566"],"orders":["BKG1001"],"bls":[]}'/></parameters>
</groovy>]]></xmlDoc>
```

The `responsePayLoad` should be JSON like:

```json
{"units":[{"containerNo":"MSCU1234566","found":true,"gkey":"123456","category":"IMPRT","freightKind":"FCL",
  "transitState":"S40_YARD","lineOperator":"MSC","vgmKg":null,"holds":["CUSTOMS HOLD"]}],
 "orders":[{"nbr":"BKG1001","found":true,"gkey":"777","subType":"BOOK","lineOperator":"HLC",
  "quantityRemaining":3,"cargoCutoff":"2026-10-21T18:00:00+03:00","holds":[]}],
 "bls":[]}
```

## 2. Configure TAS

```json
"Tos": { "Adapter": "N4", "OnUnavailable": "Warn" },
"N4": {
  "Terminals": {
    "JED1": {
      "Url": "http://n4-server:9080/apex/services/argoservice",
      "Username": "tas-ws",
      "Password": "",
      "OperatorId": "OPR", "ComplexId": "CPX", "FacilityId": "FCY", "YardId": "YRD",
      "CodeExtension": "TasLookup",
      "TimeoutSeconds": 15
    }
  },
  "HoldMapping": { "H12": "CustomsRelease" }
}
```

- The key under `Terminals` is the **TAS terminal code**. Put the password in an environment variable:
  `N4__Terminals__JED1__Password=…`
- The N4 user only needs rights to call web services and read units / orders / BLs.
- `HoldMapping` maps N4 hold ids to TAS checks when the id doesn't say what it is. Without a mapping, ids are
  classified by keywords: *CUSTOM, FASAH* → customs · *LINE, CARRIER* → line release ·
  *PAY, UNPAID, INVOICE, STORAGE, DEMURRAGE* → payment · anything else → terminal hold.

## 3. Rules TAS applies to the N4 answer

| Move | Checks |
|---|---|
| Import pickup | Unit known; category `IMPRT`; in the yard (`S40_YARD`) — not yet discharged / already gone fail; each active hold fails its check (customs, line, payment, terminal) |
| Export drop-off | Booking known with containers left; slot not after the cargo cut-off; container not already in the terminal; FCL without VGM in N4 → warning; holds |
| Empty pickup | Release / EDO known with containers left; holds |
| Empty return | Container not already in the terminal; holds |
| Breakbulk / bulk (BL) | BL known; released; holds |

**Book by BL** (several trucks at once): `blUnits` lists the containers of an import BL that are still active in N4 (announced, inbound or in the yard) with their ISO type; each is then checked like an import pickup. The unit → BL path in `blUnitsInfo` is marked VERIFY.

## 4. When N4 can't be reached

Timeouts, HTTP errors, SOAP faults, a missing payload or `{"error": …}` from the code extension all count as
*N4 unavailable*:

| Where | What happens |
|---|---|
| Booking | `OnUnavailable: Warn` (default) — booked with an amber "terminal system could not be reached" warning, and marked for the next scheduled re-check. `Reject` — booking refused, try again later |
| Scheduled re-check | Skipped; the appointment stays due and is retried on the next run |
| Re-check on request / trip start | Refused with "try again in a few minutes" — a truck is never told to set off unchecked |
| Gate | The truck is **not** admitted automatically; it goes to the trouble desk to be verified in N4 |

## 5. Not included yet (needs your go-ahead — it writes to N4)

- Pre-advising export containers / creating truck visits or gate appointments in N4 from TAS bookings.
- Receiving N4 events (discharge, hold released) to re-check immediately instead of on the schedule.
