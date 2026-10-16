#!/usr/bin/env bash
# Runs in the "backup" service: every day at BACKUP_AT (local time, TZ) a pg_dump of the database into /backups,
# keeping BACKUP_DAYS days. Restore: see deploy/README.md.
set -euo pipefail
: "${BACKUP_AT:=01:30}" "${BACKUP_DAYS:=14}"

backup_now() {
  local file="/backups/tas-$(date +%Y%m%d-%H%M%S).dump"
  echo "$(date -Is) backup to $file"
  pg_dump --format=custom --no-owner --file="$file.partial"
  mv "$file.partial" "$file"
  find /backups -name 'tas-*.dump' -mtime "+$BACKUP_DAYS" -print -delete
}

[ "${1:-}" = "now" ] && { backup_now; exit 0; }

while true; do
  now=$(date +%s)
  next=$(date -d "today $BACKUP_AT" +%s)
  [ "$next" -le "$now" ] && next=$(date -d "tomorrow $BACKUP_AT" +%s)
  echo "$(date -Is) next backup at $(date -d "@$next" -Is)"
  sleep $((next - now))
  backup_now || echo "$(date -Is) backup FAILED" >&2
done
