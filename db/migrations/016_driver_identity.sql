-- 016: the gate can refer a truck because its driver is not identified as the terminal requires
-- (terminal setting driver_identity: a named driver, or the named driver's own gate pass).

ALTER TABLE gate_trouble DROP CONSTRAINT IF EXISTS gate_trouble_reason_code_check;
ALTER TABLE gate_trouble ADD CONSTRAINT gate_trouble_reason_code_check
    CHECK (reason_code IN ('NO_APPOINTMENT','EARLY','LATE','NOT_CLEARED','BLOCKED','ALREADY_IN','CANCELLED','DRIVER_UNKNOWN'));
