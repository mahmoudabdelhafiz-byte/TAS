-- 009: the truck's last position reported by the driver app (only on the way and inside the terminal),
-- for the control room's yard map.

ALTER TABLE appointment
    ADD COLUMN last_latitude     numeric(9,6) NULL,
    ADD COLUMN last_longitude    numeric(9,6) NULL,
    ADD COLUMN last_position_at  timestamptz  NULL;
