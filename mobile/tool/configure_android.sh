#!/usr/bin/env bash
# Adds what the app needs to the generated Android manifest (until the android/ folder is committed):
# internet access (release builds) and location while the app is open (trip position, pre-gate check-in).
set -euo pipefail
manifest="android/app/src/main/AndroidManifest.xml"
for permission in INTERNET ACCESS_FINE_LOCATION ACCESS_COARSE_LOCATION; do
  if ! grep -q "android.permission.$permission" "$manifest"; then
    sed -i "0,/<manifest[^>]*>/s//&\n    <uses-permission android:name=\"android.permission.$permission\"\/>/" "$manifest"
  fi
done
grep -n "uses-permission" "$manifest"
