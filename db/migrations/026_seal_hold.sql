-- 026: a terminal can hold a truck at the entry when a seal read at the gate isn't the one the company declared
-- (terminal setting seal_hold): the case goes to the trouble desk as SEAL_MISMATCH.

ALTER TABLE gate_trouble DROP CONSTRAINT IF EXISTS gate_trouble_reason_code_check;
ALTER TABLE gate_trouble ADD CONSTRAINT gate_trouble_reason_code_check
    CHECK (reason_code IN ('NO_APPOINTMENT','EARLY','LATE','NOT_CLEARED','BLOCKED','ALREADY_IN','CANCELLED','DRIVER_UNKNOWN','SEAL_MISMATCH'));
