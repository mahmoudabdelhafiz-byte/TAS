-- 013: the terminal a message belongs to, so each terminal can send with its own providers and sender names.

ALTER TABLE notification_log ADD COLUMN IF NOT EXISTS terminal_id integer NULL REFERENCES terminal(terminal_id);

UPDATE notification_log n SET terminal_id = a.terminal_id
FROM appointment a
WHERE n.appointment_id = a.appointment_id AND n.terminal_id IS NULL;
