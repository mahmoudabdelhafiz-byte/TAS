#!/usr/bin/env bash
# Adds what the app needs to the generated Android manifest (until the android/ folder is committed):
# - internet access (release builds)
# - location for the trip, also in the background through a foreground service with a visible notification
#   (stops at the pre-gate check-in; nothing is tracked outside a trip)
# - the text-to-speech engine for spoken directions in the yard (Android 11+ must declare it to see it)
set -euo pipefail
manifest="android/app/src/main/AndroidManifest.xml"
for permission in INTERNET ACCESS_FINE_LOCATION ACCESS_COARSE_LOCATION FOREGROUND_SERVICE FOREGROUND_SERVICE_LOCATION POST_NOTIFICATIONS WAKE_LOCK; do
  if ! grep -q "android.permission.$permission\"" "$manifest"; then
    sed -i "0,/<manifest[^>]*>/s//&\n    <uses-permission android:name=\"android.permission.$permission\"\/>/" "$manifest"
  fi
done
if ! grep -q "android.intent.action.TTS_SERVICE" "$manifest"; then
  tts='        <intent>\n            <action android:name="android.intent.action.TTS_SERVICE"\/>\n        <\/intent>'
  if grep -q "<queries>" "$manifest"; then
    sed -i "0,/<queries>/s//<queries>\n$tts/" "$manifest"
  else
    sed -i "s/<\/manifest>/    <queries>\n$tts\n    <\/queries>\n<\/manifest>/" "$manifest"
  fi
fi
grep -n "uses-permission\|TTS_SERVICE" "$manifest"
