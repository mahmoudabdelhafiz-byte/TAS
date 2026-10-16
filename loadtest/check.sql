-- After a load test: the booking rules must still hold. One line: name=count; every count but "appointments" must be 0.
WITH live AS (
    SELECT * FROM appointment WHERE status IN ('CONFIRMED', 'EN_ROUTE', 'ARRIVED', 'GATE_IN', 'COMPLETED')
), per_slot AS (
    SELECT slot_id, count(*) AS n FROM live GROUP BY slot_id
), per_category AS (
    SELECT a.slot_id, i.category_id, sum(coalesce(i.trips, 1)) AS n
    FROM live a JOIN appointment_item i USING (appointment_id) GROUP BY 1, 2
)
SELECT concat_ws(' ',
    'appointments=' || (SELECT count(*) FROM live),
    -- more trucks in a slot than it takes
    'oversold_slots=' || (SELECT count(*) FROM slot WHERE total_booked > total_capacity),
    -- the slot counter disagrees with the appointments in it
    'slot_counter_drift=' || (SELECT count(*) FROM slot s LEFT JOIN per_slot p USING (slot_id) WHERE s.total_booked <> coalesce(p.n, 0)),
    -- a category counter disagrees with the moves in the slot
    'category_counter_drift=' || (SELECT count(*) FROM slot_category_capacity c LEFT JOIN per_category p USING (slot_id, category_id)
                                  WHERE c.booked <> coalesce(p.n, 0)),
    -- a category booked past its quota (empty returns may go past once released, so only imports are checked)
    'import_quota_exceeded=' || (SELECT count(*) FROM slot_category_capacity c JOIN transaction_category t USING (category_id)
                                 WHERE t.code = 'IMP_PICKUP' AND c.booked > c.quota_capacity),
    -- one container in two live appointments
    'double_booked_containers=' || (SELECT count(*) FROM (SELECT i.container_no FROM appointment_item i
                                    JOIN appointment a USING (appointment_id)
                                    WHERE a.status IN ('CONFIRMED', 'EN_ROUTE', 'ARRIVED', 'GATE_IN') AND i.container_no IS NOT NULL
                                    GROUP BY i.container_no HAVING count(*) > 1) d));
