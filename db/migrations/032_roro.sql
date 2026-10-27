-- 032: RoRo terminals. Vehicles are booked one by one by VIN (chassis number): a car carrier takes several on one
-- visit (at most the terminal's roro_max_per_truck), and a drive-away visit has no truck (the driver drives the vehicle
-- itself). A category in VEHICLE mode moves vehicles; drive_away marks the categories without a truck.

ALTER TABLE transaction_category DROP CONSTRAINT IF EXISTS transaction_category_mode_check;
ALTER TABLE transaction_category ADD CONSTRAINT transaction_category_mode_check CHECK (mode IN ('CONTAINER','CARGO','VEHICLE'));
ALTER TABLE transaction_category ADD COLUMN IF NOT EXISTS drive_away boolean NOT NULL DEFAULT false;

ALTER TABLE appointment_item ADD COLUMN IF NOT EXISTS vin varchar(17) NULL;
ALTER TABLE appointment_item ADD COLUMN IF NOT EXISTS vehicle_desc varchar(60) NULL;
ALTER TABLE appointment_item DROP CONSTRAINT IF EXISTS ck_item_kind;
ALTER TABLE appointment_item ADD CONSTRAINT ck_item_kind CHECK (
    (container_no IS NOT NULL AND cargo_allocation_id IS NULL AND vin IS NULL) OR
    (container_no IS NULL AND cargo_allocation_id IS NOT NULL AND vin IS NULL) OR
    (container_no IS NULL AND cargo_allocation_id IS NULL AND vin IS NOT NULL) OR
    (container_no IS NULL AND cargo_allocation_id IS NULL AND vin IS NULL AND booking_ref IS NOT NULL)  -- empty pickup by booking only
);
CREATE INDEX IF NOT EXISTS ix_item_vin ON appointment_item(vin) WHERE vin IS NOT NULL;

-- A RoRo terminal type: its standard move types are vehicle moves.
ALTER TABLE terminal DROP CONSTRAINT IF EXISTS terminal_terminal_type_check;
ALTER TABLE terminal ADD CONSTRAINT terminal_terminal_type_check CHECK (terminal_type IN ('CONTAINER','MULTIPURPOSE','RORO'));
