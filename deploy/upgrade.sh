#!/usr/bin/env bash
# Upgrade Yardsyncs on this server to a released version, or roll back the last upgrade.
#
#   ./upgrade.sh 1.2.0        back up the database, switch to image 1.2.0, wait until it is ready
#   ./upgrade.sh --rollback   the version before the last upgrade, with the database from the backup taken then
#   ./upgrade.sh --status     the version running and the one before
#
# The app upgrades the database when it starts, and database upgrades only go forward: going back to an older version
# needs the database as it was, which is why every upgrade starts with a backup.
#
# Settings (environment): REGISTRY_IMAGE (default ghcr.io/mahmoudabdelhafiz-byte/yardsyncs), READY_URL (default
# https://$TAS_DOMAIN/health/ready through the local proxy), COMPOSE_FILES (extra -f options), READY_SECONDS (300).
# Without internet (terminal network): `docker load < yardsyncs-1.2.0.tar.gz` first; an image already here isn't pulled.
set -euo pipefail
cd "$(dirname "$0")"

REGISTRY_IMAGE=${REGISTRY_IMAGE:-ghcr.io/mahmoudabdelhafiz-byte/yardsyncs}
READY_SECONDS=${READY_SECONDS:-300}
STATE=.upgrade-last
# shellcheck disable=SC2086
compose() { docker compose ${COMPOSE_FILES:-} "$@"; }
say() { echo "$(date '+%H:%M:%S') $*"; }

[ -f .env ] || { echo "No .env here: run this in the deploy folder of an installed server." >&2; exit 1; }
domain=$(grep -E '^TAS_DOMAIN=' .env | cut -d= -f2- || true)
READY_URL=${READY_URL:-https://$domain/health/ready}
current=$(grep -E '^YARDSYNCS_IMAGE=' .env | cut -d= -f2- || true)
current=${current:-yardsyncs:latest}

set_image() {
  if grep -qE '^YARDSYNCS_IMAGE=' .env; then
    sed -i.bak -E "s#^YARDSYNCS_IMAGE=.*#YARDSYNCS_IMAGE=$1#" .env && rm -f .env.bak
  else
    printf '\nYARDSYNCS_IMAGE=%s\n' "$1" >> .env
  fi
}

ready() {
  local resolve=()
  [ -n "$domain" ] && [[ "$READY_URL" == https://$domain/* ]] && resolve=(--resolve "$domain:443:127.0.0.1")
  for _ in $(seq 1 $((READY_SECONDS / 5))); do
    if curl -fsSk "${resolve[@]}" "$READY_URL" -o /tmp/yardsyncs-ready.json 2>/dev/null; then
      say "ready: $(cat /tmp/yardsyncs-ready.json)"
      return 0
    fi
    sleep 5
  done
  return 1
}

start() {
  docker image inspect "$1" >/dev/null 2>&1 || { say "pulling $1"; docker pull "$1"; }
  set_image "$1"
  compose up -d --no-build app
}

case "${1:-}" in
  --status)
    echo "running:  $current"
    [ -f "$STATE" ] && echo "before:   $(cut -d' ' -f1 "$STATE") (backup $(cut -d' ' -f2 "$STATE"))"
    ;;

  --rollback)
    [ -f "$STATE" ] || { echo "No upgrade to roll back (no $STATE)." >&2; exit 1; }
    read -r previous backup < "$STATE"
    [ -f "$backup" ] || { echo "The backup $backup is missing; restore one by hand (deploy/README.md)." >&2; exit 1; }
    say "rolling back $current -> $previous with the database from $backup"
    compose stop app
    compose exec -T db dropdb -U tas --force tas
    compose exec -T db createdb -U tas tas
    compose exec -T db pg_restore -U tas -d tas --no-owner < "$backup"
    start "$previous"
    ready || { say "NOT READY after the rollback: see 'docker compose logs app'"; exit 1; }
    rm -f "$STATE"
    say "rolled back to $previous"
    ;;

  ""|-*)
    sed -n '2,15p' "$0"; exit 1
    ;;

  *)
    target="$1"
    [[ "$target" == */* || "$target" == *:* ]] && image="$target" || image="$REGISTRY_IMAGE:$target"
    [ "$image" = "$current" ] && { say "already running $image"; exit 0; }
    docker image inspect "$image" >/dev/null 2>&1 || { say "pulling $image"; docker pull "$image"; }

    say "backup before the upgrade"
    compose run --rm backup now
    backup=$(ls -t backups/tas-*.dump | head -1)
    echo "$current $backup" > "$STATE"

    say "upgrading $current -> $image"
    start "$image"
    if ready; then
      say "upgraded to $image (back with: ./upgrade.sh --rollback)"
    else
      say "NOT READY after ${READY_SECONDS}s: see 'docker compose logs app'. To go back: ./upgrade.sh --rollback"
      exit 1
    fi
    ;;
esac
