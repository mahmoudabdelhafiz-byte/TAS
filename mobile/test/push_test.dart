import 'package:flutter/foundation.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:yardsyncs_mobile/push.dart';

void main() {
  const full = PushConfig(projectId: 'yardsyncs-demo', senderId: '1234', apiKey: 'key', androidAppId: '1:1234:android:ab', iosAppId: '1:1234:ios:cd', iosBundleId: 'com.yardsyncs.mobile');

  test('push is set up per platform from the build settings', () {
    final android = full.optionsFor(TargetPlatform.android)!;
    expect(android.appId, '1:1234:android:ab');
    expect(android.projectId, 'yardsyncs-demo');
    expect(full.optionsFor(TargetPlatform.iOS)!.iosBundleId, 'com.yardsyncs.mobile');
    expect(full.optionsFor(TargetPlatform.windows), isNull);

    const androidOnly = PushConfig(projectId: 'p', senderId: 's', apiKey: 'k', androidAppId: 'a', iosAppId: '', iosBundleId: 'b');
    expect(androidOnly.optionsFor(TargetPlatform.iOS), isNull);
    // A build without Firebase settings: no push, messages come by SMS.
    expect(const PushConfig.fromEnvironment().optionsFor(TargetPlatform.android), isNull);
  });

  test('messages carry the template and the appointment', () {
    final m = PushMessage.fromData({'template': 'GATE_IN', 'appointmentNo': 'DEMO-261015-8MZ1K'}, body: 'Go to B2 (14.03.2)', opened: true);
    expect(m.isGateIn, isTrue);
    expect(m.appointmentNo, 'DEMO-261015-8MZ1K');
    expect(m.opened, isTrue);
    expect(PushMessage.fromData({}).isGateIn, isFalse);
  });
}
