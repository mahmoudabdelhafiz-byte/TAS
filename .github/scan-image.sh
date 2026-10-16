#!/usr/bin/env bash
# Known vulnerabilities in a Yardsyncs image (Grype), reported as CI annotations.
#   scan-image.sh IMAGE [strict]
# Only problems that already have a fix are counted (those are the ones an update can remove). Critical ones fail a
# release ("strict" = true); otherwise everything is a warning, and Dependabot / a rebuild brings the fixes.
set -euo pipefail
image=$1
strict=${2:-false}

docker run --rm -v /var/run/docker.sock:/var/run/docker.sock anchore/grype:v0.82.0 \
  "$image" --only-fixed -o json > grype.json 2> grype.log || { tail -20 grype.log; echo "::warning title=image scan::scan did not run"; exit 0; }

jq -r '[.matches[].vulnerability.severity] | group_by(.) | map("\(.[0]) \(length)") | join(", ") | if . == "" then "none with a fix available" else . end' grype.json \
  | while read -r line; do echo "::notice title=image scan::$line"; done
jq -r '.matches[] | select(.vulnerability.severity == "Critical" or .vulnerability.severity == "High") |
  "\(.vulnerability.severity) \(.vulnerability.id) in \(.artifact.name) \(.artifact.version) → fixed in \(.vulnerability.fix.versions | join(" / "))"' grype.json \
  | sort -u | head -30 | while read -r line; do echo "::warning title=image scan::$line"; done

critical=$(jq '[.matches[] | select(.vulnerability.severity == "Critical")] | length' grype.json)
if [ "$strict" = "true" ] && [ "$critical" -gt 0 ]; then
  echo "::error title=image scan::$critical critical vulnerabilities with a fix: update the base image or the package before releasing"
  exit 1
fi
