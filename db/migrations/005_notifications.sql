-- Notification sender (schema v0.5 -> v0.6): delivery attempts, retries, recipient, SKIPPED status.

ALTER TABLE notification_log
    ADD COLUMN recipient       varchar(200) NULL,
    ADD COLUMN attempts        integer      NOT NULL DEFAULT 0,
    ADD COLUMN next_attempt_at timestamptz  NULL,
    ADD COLUMN last_error      varchar(300) NULL;

ALTER TABLE notification_log DROP CONSTRAINT notification_log_status_check;
ALTER TABLE notification_log ADD CONSTRAINT notification_log_status_check
    CHECK (status IN ('QUEUED','SENT','DELIVERED','FAILED','SKIPPED'));

CREATE INDEX ix_notification_due ON notification_log(next_attempt_at, created_at) WHERE status = 'QUEUED';
CREATE INDEX ix_notification_appt ON notification_log(appointment_id, template_code);
