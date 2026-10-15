-- Notification delivery (schema v0.5 -> v0.6).

ALTER TABLE notification_log
    ADD COLUMN params          jsonb        NULL,
    ADD COLUMN recipient       varchar(200) NULL,
    ADD COLUMN attempts        integer      NOT NULL DEFAULT 0,
    ADD COLUMN last_error      varchar(400) NULL,
    ADD COLUMN next_attempt_at timestamptz  NULL;

CREATE INDEX ix_notification_due ON notification_log(status, next_attempt_at);
CREATE INDEX ix_notification_appt ON notification_log(appointment_id, template_code);
