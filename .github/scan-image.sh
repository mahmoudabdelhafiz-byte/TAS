#!/usr/bin/env bash
# Known vulnerabilities in a Yardsyncs image (Grype), reported as CI annotations.
#   scan-image.sh IMAGE [strict]
# strict=true (a release): the scan must run, and a critical vulnerability that has a fix fails the release (update the
# base image or the package). Critical ones without a fix yet are listed for review, not blocking. Otherwise (pull
# requests) everything is a warning, and Dependabot / a rebuild brings the fixes.
set -euo pipefail
image=$1
strict=${2:-false}

if ! docker run --rm -v /var/run/docker.sock:/var/run/docker.sock anchore/grype:v0.82.0 \
    "$image" -o json > grype.json 2> grype.log || ! jq -e '.matches' grype.json > /dev/null 2>&1; then
  tail -20 grype.log || true
  if [ "$strict" = "true" ]; then
    echo "::error title=image scan::the vulnerability scan did not run: a release is not published without one"
    exit 1
  fi
  echo "::warning title=image scan::the vulnerability scan did not run"
  exit 0
fi

fixed='.vulnerability.fix.state == "fixed"'
jq -r "[.matches[] | select($fixed) | .vulnerability.severity] | group_by(.) | map(\"\(.[0]) \(length)\") | join(\", \") | if . == \"\" then \"none with a fix available\" else . end" grype.json \
  | while read -r line; do echo "::notice title=image scan (fix available)::$line"; done
jq -r ".matches[] | select($fixed) | select(.vulnerability.severity == \"Critical\" or .vulnerability.severity == \"High\") |
  \"\(.vulnerability.severity) \(.vulnerability.id) in \(.artifact.name) \(.artifact.version) → fixed in \(.vulnerability.fix.versions | join(\" / \"))\"" grype.json \
  | sort -u | head -30 | while read -r line; do echo "::warning title=image scan::$line"; done
# No fix published yet: nothing an update can do, but someone should look at whether it matters here.
jq -r ".matches[] | select(($fixed) | not) | select(.vulnerability.severity == \"Critical\") |
  \"Critical \(.vulnerability.id) in \(.artifact.name) \(.artifact.version): no fix yet — review whether it applies\"" grype.json \
  | sort -u | head -20 | while read -r line; do echo "::warning title=image scan (no fix yet)::$line"; done

critical=$(jq "[.matches[] | select($fixed) | select(.vulnerability.severity == \"Critical\")] | length" grype.json)
if [ "$strict" = "true" ] && [ "$critical" -gt 0 ]; then
  echo "::error title=image scan::$critical critical vulnerabilities with a fix: update the base image or the package before releasing"
  exit 1
fi
