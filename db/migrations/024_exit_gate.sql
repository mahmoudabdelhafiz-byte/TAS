-- 024: the exit gate. Each move type says which way its cargo goes (IN: the truck brings it into the terminal, e.g.
-- export drop-off; OUT: the truck takes it out, e.g. import pickup). At gate-out the containers seen on the truck are
-- checked against the visit: what should leave is on it, what should have been dropped is not. Each exit is recorded,
-- with any problem, who let the truck out anyway, and the truck's weight in and out.

ALTER TABLE transaction_category ADD COLUMN IF NOT EXISTS flow varchar(3) NULL CHECK (flow IN ('IN','OUT'));
UPDATE transaction_category SET flow = 'OUT' WHERE flow IS NULL AND (code LIKE '%PICKUP%' OR code LIKE '%DELIVERY%');
UPDATE transaction_category SET flow = 'IN' WHERE flow IS NULL AND (code LIKE '%DROP%' OR code LIKE '%RETURN%' OR code LIKE '%RECEIV%');

CREATE TABLE IF NOT EXISTS gate_exit_check (
    appointment_id   bigint        PRIMARY KEY REFERENCES appointment(appointment_id) ON DELETE CASCADE,
    terminal_id      integer       NOT NULL REFERENCES terminal(terminal_id),
    checked_at       timestamptz   NOT NULL,
    lane_code        varchar(20)   NULL,
    containers_seen  varchar(60)   NULL,   -- comma-separated; NULL = nothing read
    status           varchar(11)   NOT NULL CHECK (status IN ('OK','PROBLEM','NOT_CHECKED')),
    problems         varchar(600)  NULL,
    let_out_by       integer       NULL,   -- app_user.user_id when a clerk let the truck out despite a problem
    note             varchar(300)  NULL,
    weight_in_kg     numeric(9,1)  NULL,
    weight_out_kg    numeric(9,1)  NULL
);
CREATE INDEX IF NOT EXISTS ix_exit_check_time ON gate_exit_check (terminal_id, checked_at);
