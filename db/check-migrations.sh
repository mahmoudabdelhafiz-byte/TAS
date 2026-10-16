#!/usr/bin/env bash
# Checks that the migrations added on this branch turn the previous db/schema.sql into the current one:
#   previous schema.sql + new migrations  ==  current schema.sql   (tables, columns, constraints, indexes)
# Usage: db/check-migrations.sh <base-commit>   (PG* environment variables point at a PostgreSQL with PostGIS)
set -euo pipefail
base="${1:?base commit}"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

git show "$base:db/schema.sql" > "$work/previous.sql"
mapfile -t added < <(comm -13 <(git ls-tree --name-only "$base" db/migrations/ | sort) <(ls db/migrations/*.sql | sort))

run() { psql -v ON_ERROR_STOP=1 -q -X "$@"; }
for db in tas_check_upgraded tas_check_fresh; do
  run -d postgres -c "DROP DATABASE IF EXISTS $db" -c "CREATE DATABASE $db"
done

run -d tas_check_upgraded -f "$work/previous.sql"
for file in "${added[@]}"; do
  echo "Applying $file"
  run -d tas_check_upgraded -f "$file"
  # Migrations must be re-runnable (IF NOT EXISTS / IF EXISTS): the app may meet a half-known state.
  run -d tas_check_upgraded -f "$file"
done
run -d tas_check_fresh -f db/schema.sql

run -d tas_check_upgraded -At -f db/catalog.sql > "$work/upgraded.txt"
run -d tas_check_fresh -At -f db/catalog.sql > "$work/fresh.txt"
if ! diff -u "$work/upgraded.txt" "$work/fresh.txt" > "$work/diff.txt"; then
  echo "db/schema.sql and the migrations disagree (- upgraded with migrations, + fresh from schema.sql):"
  cat "$work/diff.txt"
  while read -r line; do echo "::error title=schema drift::$line"; done < <(grep -E '^[-+][a-z]' "$work/diff.txt" | head -20)
  exit 1
fi
echo "Schema check passed: ${#added[@]} new migration(s); previous schema + migrations = schema.sql ($(wc -l < "$work/fresh.txt") items)."
