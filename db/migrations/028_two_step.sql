-- 028: two-step sign-in with an authenticator app. Required for terminal, operator and system admins (setting
-- Auth:AdminTwoStep), possible for everyone. The secret is stored encrypted; the last code used can't be used again.

ALTER TABLE app_user ADD COLUMN IF NOT EXISTS totp_secret bytea NULL;            -- encrypted (AES-GCM); set while setting up
ALTER TABLE app_user ADD COLUMN IF NOT EXISTS totp_enabled_at timestamptz NULL;  -- confirmed with a first code: on
ALTER TABLE app_user ADD COLUMN IF NOT EXISTS totp_last_step bigint NULL;        -- the 30-second step of the last code used
