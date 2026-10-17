// Push notifications through Firebase Cloud Messaging (Android, and iPhone via Apple's push service).
//
// The Firebase project is set per installation at build time, so no Firebase file is committed:
//   flutter build apk --dart-define=FIREBASE_PROJECT_ID=… --dart-define=FIREBASE_SENDER_ID=…
//     --dart-define=FIREBASE_API_KEY=… --dart-define=FIREBASE_ANDROID_APP_ID=… --dart-define=FIREBASE_IOS_APP_ID=…
// Without them the app works as before and messages arrive by SMS.

import 'dart:async';

import 'package:firebase_core/firebase_core.dart';
import 'package:firebase_messaging/firebase_messaging.dart';
import 'package:flutter/foundation.dart';

import 'api.dart';

class PushConfig {
  const PushConfig({
    required this.projectId,
    required this.senderId,
    required this.apiKey,
    required this.androidAppId,
    required this.iosAppId,
    required this.iosBundleId,
  });

  /// From --dart-define at build time.
  const PushConfig.fromEnvironment()
      : projectId = const String.fromEnvironment('FIREBASE_PROJECT_ID'),
        senderId = const String.fromEnvironment('FIREBASE_SENDER_ID'),
        apiKey = const String.fromEnvironment('FIREBASE_API_KEY'),
        androidAppId = const String.fromEnvironment('FIREBASE_ANDROID_APP_ID'),
        iosAppId = const String.fromEnvironment('FIREBASE_IOS_APP_ID'),
        iosBundleId = const String.fromEnvironment('FIREBASE_IOS_BUNDLE_ID', defaultValue: 'com.yardsyncs.mobile');

  final String projectId;
  final String senderId;
  final String apiKey;
  final String androidAppId;
  final String iosAppId;
  final String iosBundleId;

  /// Firebase options for this phone's platform, or null when push isn't set up for it.
  FirebaseOptions? optionsFor(TargetPlatform platform) {
    if (projectId.isEmpty || senderId.isEmpty || apiKey.isEmpty) return null;
    return switch (platform) {
      TargetPlatform.android when androidAppId.isNotEmpty =>
        FirebaseOptions(apiKey: apiKey, appId: androidAppId, messagingSenderId: senderId, projectId: projectId),
      TargetPlatform.iOS when iosAppId.isNotEmpty =>
        FirebaseOptions(apiKey: apiKey, appId: iosAppId, messagingSenderId: senderId, projectId: projectId, iosBundleId: iosBundleId),
      _ => null,
    };
  }
}

/// A message from the terminal: which template (GATE_IN, STATUS_RED…) and for which appointment.
class PushMessage {
  const PushMessage({required this.template, required this.appointmentNo, this.title, this.body, this.opened = false});

  final String template;
  final String appointmentNo;
  final String? title;
  final String? body;

  /// The driver tapped the notification (the app was in the background).
  final bool opened;

  factory PushMessage.fromData(Map<String, dynamic> data, {String? title, String? body, bool opened = false}) => PushMessage(
        template: (data['template'] as String?) ?? '',
        appointmentNo: (data['appointmentNo'] as String?) ?? '',
        title: title,
        body: body,
        opened: opened,
      );

  bool get isGateIn => template == 'GATE_IN';

  /// A message from the control room about a visit.
  bool get isControlRoomMessage => template == 'CONTROL_ROOM_MESSAGE';

  /// The terminal recorded an entry on the driver's record.
  bool get isRecordEntry => template == 'DRIVER_INCIDENT';
}

class PushService {
  PushService(this._api, {this.config = const PushConfig.fromEnvironment()});

  final DriverApi _api;
  final PushConfig config;
  final _messages = StreamController<PushMessage>.broadcast();
  bool _started = false;

  /// Messages while the app is open, and taps on notifications.
  Stream<PushMessage> get messages => _messages.stream;

  /// Asks for permission (Android 13+, iPhone), registers this phone's token with the terminal.
  /// Safe to call on every start; does nothing when push isn't configured or Firebase can't start.
  Future<void> start() async {
    if (_started) return;
    final options = config.optionsFor(defaultTargetPlatform);
    if (options == null) return;
    try {
      if (Firebase.apps.isEmpty) await Firebase.initializeApp(options: options);
      final messaging = FirebaseMessaging.instance;
      await messaging.requestPermission(alert: true, badge: true, sound: true);
      _started = true;

      final token = await messaging.getToken();
      if (token != null) await _api.registerPushToken(token);
      messaging.onTokenRefresh.listen((t) => _api.registerPushToken(t).catchError((_) {}));

      FirebaseMessaging.onMessage.listen((m) => _messages.add(
          PushMessage.fromData(m.data, title: m.notification?.title, body: m.notification?.body)));
      FirebaseMessaging.onMessageOpenedApp.listen((m) => _messages.add(PushMessage.fromData(m.data, opened: true)));
      final initial = await messaging.getInitialMessage();
      if (initial != null) _messages.add(PushMessage.fromData(initial.data, opened: true));
    } catch (e) {
      // No Google Play services, no network, Firebase not set up for this build: SMS still works.
      debugPrint('Push not available: $e');
    }
  }
}
