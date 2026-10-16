# Database

- `schema.sql` — the whole current schema. A new database is created from it.
- `migrations/NNN_name.sql` — the changes from one version to the next, applied in order to existing databases.
- `catalog.sql`, `check-migrations.sh` — the check CI runs on every pull request: the previous `schema.sql` plus the
  new migrations must give exactly the current `schema.sql` (tables, columns, constraints, indexes).

The application ships both inside its build and creates or upgrades the database itself on start
(`Database:MigrateOnStartup`, default on), or with `dotnet Yardsyncs.Api.dll migrate`. Applied versions are kept in the
table `schema_migration`.

## Changing the schema

1. Add `migrations/NNN_what.sql` with the next number.
2. Make the same change in `schema.sql` (and raise its draft version in the header).
3. Write migrations so they can run twice without harm: `CREATE TABLE IF NOT EXISTS`, `ADD COLUMN IF NOT EXISTS`,
   `CREATE INDEX IF NOT EXISTS`, `DROP … IF EXISTS`. CI runs each new migration twice.
4. Never change a migration that is already released; add a new one.
5. Large tables: add columns as `NULL` (or with a constant default), backfill in batches, add `NOT NULL` later.

Databases created by hand from `schema.sql` before version history existed (2026-10-16) are taken to be at
version 11 the first time the application meets them.
