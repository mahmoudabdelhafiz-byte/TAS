-- 021: the remote clerk station. Clerks call the driver over the lane intercom (CALL / HANGUP commands) and correct
-- what the cameras misread, then have the lane check again; corrections are kept for the OCR read rate.

ALTER TABLE lane_device_command DROP CONSTRAINT IF EXISTS lane_device_command_command_check;
ALTER TABLE lane_device_command ADD CONSTRAINT lane_device_command_command_check
    CHECK (command IN ('OPEN','CLOSE','RED','GREEN','OFF','SHOW','CLEAR','PRINT','CALL','HANGUP'));

ALTER TABLE lane_transaction ADD COLUMN IF NOT EXISTS plate_corrected boolean NOT NULL DEFAULT false;
ALTER TABLE lane_transaction ADD COLUMN IF NOT EXISTS containers_corrected boolean NOT NULL DEFAULT false;
ALTER TABLE lane_transaction ADD COLUMN IF NOT EXISTS corrected_by integer NULL;   -- app_user.user_id
ALTER TABLE lane_transaction ADD COLUMN IF NOT EXISTS corrected_at timestamptz NULL;
