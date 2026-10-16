#!/usr/bin/env bash
# Runs deploy/upgrade.sh VERSION on a server over SSH (release workflow, environments staging / production).
# Skips with a notice when the environment has no server configured yet.
set -euo pipefail
version=$1
if [ -z "${DEPLOY_HOST:-}" ] || [ -z "${DEPLOY_SSH_KEY:-}" ]; then
  echo "::notice title=deploy::no server configured for this environment yet; image $version is published, nothing deployed"
  exit 0
fi
install -m 700 -d ~/.ssh
printf '%s\n' "$DEPLOY_SSH_KEY" > ~/.ssh/deploy_key && chmod 600 ~/.ssh/deploy_key
printf '%s\n' "${DEPLOY_KNOWN_HOSTS:?set DEPLOY_KNOWN_HOSTS (ssh-keyscan of the server) so the server is verified}" > ~/.ssh/known_hosts
ssh -i ~/.ssh/deploy_key -o BatchMode=yes "${DEPLOY_USER:-yardsyncs}@$DEPLOY_HOST" \
  "cd '${DEPLOY_PATH:-/opt/yardsyncs/deploy}' && ./upgrade.sh '$version'"
echo "::notice title=deploy::$DEPLOY_HOST upgraded to $version"
