-- 007: the terminal's internal roads, for directions inside the yard.
-- Blocks, gates and exits are areas in the geofence table; roads are a GeoJSON FeatureCollection of LineStrings
-- (property "oneWay": true for one-way roads, drawn in the driving direction).

CREATE TABLE terminal_map (
    terminal_id  integer      PRIMARY KEY REFERENCES terminal(terminal_id),
    roads        jsonb        NOT NULL,
    updated_at   timestamptz  NOT NULL DEFAULT now(),
    updated_by   integer      NULL REFERENCES app_user(user_id)
);
