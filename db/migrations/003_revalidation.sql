-- Re-validation of booked appointments (schema v0.3 -> v0.4).

ALTER TABLE appointment ADD COLUMN last_validated_at timestamptz NULL;

-- Existing appointments count as checked when they were booked.
UPDATE appointment SET last_validated_at = created_at;

CREATE INDEX ix_appt_revalidation ON appointment(status, last_validated_at);
