-- The shape of the database as sorted text (tables, columns, constraints, indexes), to compare two databases.
-- Column order is ignored: ALTER TABLE ADD COLUMN appends, while db/schema.sql may list the column elsewhere.
SELECT line FROM (
    SELECT format('column %s.%s %s%s%s %s default=%s identity=%s',
                  c.table_name, c.column_name,
                  CASE WHEN c.data_type = 'USER-DEFINED' THEN c.udt_name ELSE c.data_type END,
                  coalesce('(' || c.character_maximum_length || ')', ''),
                  coalesce('(' || c.numeric_precision || ',' || c.numeric_scale || ')', ''),
                  CASE WHEN c.is_nullable = 'YES' THEN 'null' ELSE 'not null' END,
                  coalesce(c.column_default, '-'), c.is_identity) AS line
    FROM information_schema.columns c
    WHERE c.table_schema = 'public' AND c.table_name NOT IN ('spatial_ref_sys', 'schema_migration')
      AND c.table_name IN (SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' AND table_type = 'BASE TABLE')
    UNION ALL
    SELECT format('constraint %s %s', conrelid::regclass, pg_get_constraintdef(oid))
    FROM pg_constraint
    WHERE connamespace = 'public'::regnamespace AND conrelid::regclass::text NOT IN ('spatial_ref_sys', 'schema_migration')
    UNION ALL
    SELECT format('index %s', indexdef)
    FROM pg_indexes
    WHERE schemaname = 'public' AND tablename NOT IN ('spatial_ref_sys', 'schema_migration')
) shape
ORDER BY line;
