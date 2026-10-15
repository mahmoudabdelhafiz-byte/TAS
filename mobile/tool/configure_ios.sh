#!/usr/bin/env bash
# Adds what the app needs to the generated iOS project (until the ios/ folder is committed). Runs on macOS.
# - name "Yardsyncs mobile", bundle id com.yardsyncs.mobile
# - location while in use and in the background during a trip, with the texts iOS shows the driver
# - push notifications (remote-notification background mode); the Push capability and the APNs key are set up
#   with the Apple developer account when signing (see mobile/README.md)
# - iOS 13 or later (Firebase)
set -euo pipefail
plist="ios/Runner/Info.plist"
pbx="ios/Runner.xcodeproj/project.pbxproj"

set_string() { plutil -replace "$1" -string "$2" "$plist"; }
set_string CFBundleDisplayName "Yardsyncs mobile"
set_string CFBundleName "Yardsyncs mobile"
set_string NSLocationWhenInUseUsageDescription "Yardsyncs mobile shares your position with the terminal only while you are on the way to it, and guides you to your container inside it."
set_string NSLocationAlwaysAndWhenInUseUsageDescription "During a trip Yardsyncs mobile keeps sharing your position with the terminal when the phone is locked, until you reach the gate, and inside the terminal until you leave."
plutil -replace UIBackgroundModes -json '["location","remote-notification"]' "$plist"

sed -i '' 's/PRODUCT_BUNDLE_IDENTIFIER = com\.yardsyncs\.[A-Za-z_]*;/PRODUCT_BUNDLE_IDENTIFIER = com.yardsyncs.mobile;/' "$pbx"
sed -i '' 's/PRODUCT_BUNDLE_IDENTIFIER = com\.yardsyncs\.[A-Za-z_]*\.RunnerTests;/PRODUCT_BUNDLE_IDENTIFIER = com.yardsyncs.mobile.RunnerTests;/' "$pbx"
sed -i '' 's/IPHONEOS_DEPLOYMENT_TARGET = 1[0-2]\.[0-9];/IPHONEOS_DEPLOYMENT_TARGET = 13.0;/g' "$pbx"

# CocoaPods: iOS 13 for the Firebase pods. Flutter writes the Podfile on the first build if it is missing.
if [ -f ios/Podfile ]; then
  sed -i '' "s/^#* *platform :ios, '[0-9.]*'/platform :ios, '13.0'/" ios/Podfile
else
  cat > ios/Podfile <<'POD'
platform :ios, '13.0'

# CocoaPods analytics sends network stats synchronously affecting flutter build latency.
ENV['COCOAPODS_DISABLE_STATS'] = 'true'

project 'Runner', {
  'Debug' => :debug,
  'Profile' => :release,
  'Release' => :release,
}

def flutter_root
  generated_xcode_build_settings_path = File.expand_path(File.join('..', 'Flutter', 'Generated.xcconfig'), __FILE__)
  unless File.exist?(generated_xcode_build_settings_path)
    raise "#{generated_xcode_build_settings_path} must exist. If you're running pod install manually, make sure flutter pub get is executed first"
  end

  File.foreach(generated_xcode_build_settings_path) do |line|
    matches = line.match(/FLUTTER_ROOT\=(.*)/)
    return matches[1].strip if matches
  end
  raise "FLUTTER_ROOT not found in #{generated_xcode_build_settings_path}. Try deleting Generated.xcconfig, then run flutter pub get"
end

require File.expand_path(File.join('packages', 'flutter_tools', 'bin', 'podhelper'), flutter_root)

flutter_ios_podfile_setup

target 'Runner' do
  use_frameworks!

  flutter_install_all_ios_pods File.dirname(File.realpath(__FILE__))
  target 'RunnerTests' do
    inherit! :search_paths
  end
end

post_install do |installer|
  installer.pods_project.targets.each do |target|
    flutter_additional_ios_build_settings(target)
    target.build_configurations.each do |config|
      config.build_settings['IPHONEOS_DEPLOYMENT_TARGET'] = '13.0'
    end
  end
end
POD
fi
grep -n "platform :ios" ios/Podfile
grep -c "com.yardsyncs.mobile" "$pbx"
plutil -p "$plist" | grep -E "Display|Location|UIBackground" -A3 | head -20
