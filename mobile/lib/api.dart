import 'dart:convert';
import 'dart:math';

import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:http/http.dart' as http;

import 'gate_pass.dart';
import 'brand.dart';
import 'models.dart';
import 'strings.dart';
import 'trip_tracker.dart';
import 'yard.dart';

/// The API address; set at build time: flutter build apk --dart-define=YARDSYNCS_API=https://tas.example.com
const apiBase = String.fromEnvironment('YARDSYNCS_API', defaultValue: 'http://10.0.2.2:5000');

/// A refusal from the server with its explanation (problem details "detail").
class ApiException implements Exception {
  ApiException(this.status, this.message);

  final int status;
  final String message;

  bool get signedOut => status == 401;

  @override
  String toString() => message;
}

/// Where the session and install id live: the phone's secure storage (Keychain / Keystore).
abstract class SessionStore {
  Future<String?> read(String key);
  Future<void> write(String key, String? value);
}

class SecureSessionStore implements SessionStore {
  const SecureSessionStore();

  static const _storage = FlutterSecureStorage(); // Android: encrypted with keys in the Android Keystore (flutter_secure_storage 10+)

  @override
  Future<String?> read(String key) => _storage.read(key: key);

  @override
  Future<void> write(String key, String? value) =>
      value == null ? _storage.delete(key: key) : _storage.write(key: key, value: value);
}

class DriverApi {
  DriverApi({SessionStore store = const SecureSessionStore(), http.Client? client, String base = apiBase})
      : _store = store,
        _client = client ?? http.Client(),
        _base = base;

  final SessionStore _store;
  final http.Client _client;
  final String _base;
  String? _token;

  /// The app's language: the server writes reasons and errors (holds from the terminal, the gate's answer) in it.
  String language = 'ar';

  static const _tokenKey = 'session.token';
  static const _installKey = 'install.id';
  static const _languageKey = 'language';
  static const _locationNoticeKey = 'location.notice';
  static const _passKey = 'gate.pass';
  static const _voiceKey = 'voice';

  /// Shares the position while on the way (one per app).
  late final TripTracker tracker = TripTracker(this);

  /// Loads a saved session; true when the app is signed in.
  Future<bool> restore() async {
    _token = await _store.read(_tokenKey);
    return _token != null;
  }

  Future<String?> savedLanguage() async {
    final lang = await _store.read(_languageKey);
    if (lang != null) language = lang;
    return lang;
  }

  Future<void> saveLanguage(String lang) {
    language = lang;
    return _store.write(_languageKey, lang);
  }

  /// Spoken directions in the yard: on unless the driver turned them off.
  Future<bool> voiceOn() async => await _store.read(_voiceKey) != 'off';
  Future<void> saveVoiceOn(bool on) => _store.write(_voiceKey, on ? null : 'off');

  /// Whether the driver has read and agreed to the location notice (asked once, before the first trip).
  Future<bool> locationNoticeAccepted() async => await _store.read(_locationNoticeKey) == '1';
  Future<void> acceptLocationNotice() => _store.write(_locationNoticeKey, '1');

  /// A random id for this install, made once (the server binds the session to it).
  Future<String> installId() async {
    final saved = await _store.read(_installKey);
    if (saved != null) return saved;
    final random = Random.secure();
    final id = List.generate(24, (_) => 'abcdefghijklmnopqrstuvwxyz0123456789'[random.nextInt(36)]).join();
    await _store.write(_installKey, id);
    return id;
  }

  /// The brand to show (public). Null when the server can't be reached.
  Future<AppBrand?> branding() async {
    try {
      final json = await _send('GET', '/api/branding', auth: false) as Map<String, dynamic>;
      return AppBrand.fromJson(json, _base);
    } catch (_) {
      return null;
    }
  }

  /// Language packs on the server with texts for the app: more languages, or corrected texts. Best effort.
  Future<void> loadLanguagePacks() async {
    try {
      final list = await _send('GET', '/api/languages', auth: false) as List<dynamic>;
      for (final item in list.cast<Map<String, dynamic>>()) {
        if (item['fromPack'] != true || item['app'] != true) continue;
        final code = item['code'] as String;
        final pack = await _send('GET', '/api/languages/${Uri.encodeComponent(code)}', auth: false) as Map<String, dynamic>;
        final texts = ((pack['app'] as Map<String, dynamic>?) ?? const {}).map((k, v) => MapEntry(k, '$v'));
        addLanguagePack(code, item['name'] as String? ?? code, item['direction'] == 'rtl', texts);
      }
    } catch (_) {
      // Offline or an older server: the built-in languages.
    }
  }

  /// How mobile numbers are typed in the installation's country, e.g. "05XXXXXXXX" (public; null when unreachable).
  Future<String?> mobileExample() async {
    try {
      final json = await _send('GET', '/api/country', auth: false) as Map<String, dynamic>;
      return json['mobileExample'] as String?;
    } catch (_) {
      return null;
    }
  }

  Future<void> requestCode(String mobile) async {
    await _send('POST', '/api/driver/login/start', body: {'mobile': mobile, 'installId': await installId()}, auth: false);
  }

  Future<DriverProfile> verify(String mobile, String code, {String platform = 'android', String appVersion = '0.1.0'}) async {
    final json = await _send('POST', '/api/driver/login/verify', body: {
      'mobile': mobile,
      'installId': await installId(),
      'code': code,
      'platform': platform,
      'appVersion': appVersion,
    }, auth: false) as Map<String, dynamic>;
    _token = json['token'] as String;
    await _store.write(_tokenKey, _token);
    await _store.write(_passKey, null); // a new sign-in means a new gate pass
    return DriverProfile.fromJson(json['driver'] as Map<String, dynamic>);
  }

  Future<DriverProfile> me() async => DriverProfile.fromJson(await _send('GET', '/api/driver/me') as Map<String, dynamic>);

  Future<List<DriverAppointment>> appointments() async {
    final list = await _send('GET', '/api/driver/appointments') as List<dynamic>;
    return list.map((e) => DriverAppointment.fromJson(e as Map<String, dynamic>)).toList();
  }

  Future<DriverAppointment> startTrip(String appointmentNo) async => DriverAppointment.fromJson(
      await _send('POST', '/api/driver/appointments/${Uri.encodeComponent(appointmentNo)}/start-trip') as Map<String, dynamic>);

  /// The visit's messages with the control room (opening them marks the control room's ones read).
  Future<List<ChatMessage>> messages(String appointmentNo) async {
    final json = await _send('GET', '/api/driver/appointments/${Uri.encodeComponent(appointmentNo)}/messages') as Map<String, dynamic>;
    return ((json['data'] as List?) ?? const []).map((e) => ChatMessage.fromJson(e as Map<String, dynamic>)).toList();
  }

  /// Sends a ready-made message (its code) or a text.
  Future<ChatMessage> sendMessage(String appointmentNo, {String? templateCode, String? text, double? latitude, double? longitude}) async {
    final json = await _send('POST', '/api/driver/appointments/${Uri.encodeComponent(appointmentNo)}/messages', body: {
      if (templateCode != null) 'templateCode': templateCode,
      if (text != null) 'text': text,
      if (latitude != null) 'latitude': latitude,
      if (longitude != null) 'longitude': longitude,
    }) as Map<String, dynamic>;
    return ChatMessage.fromJson(json['data'] as Map<String, dynamic>);
  }

  /// Ready-made messages to the control room, in the app's language.
  Future<List<QuickMessage>> quickMessages() async {
    final list = await _send('GET', '/api/driver/message-templates') as List<dynamic>;
    return list.map((e) => QuickMessage.fromJson(e as Map<String, dynamic>)).toList();
  }

  /// The gate pass secret; kept on the phone so the QR works without mobile data at the gate.
  Future<GatePass?> gatePass({bool refresh = false}) async {
    if (!refresh) {
      final saved = await _store.read(_passKey);
      if (saved != null) return GatePass.fromJson(jsonDecode(saved) as Map<String, dynamic>);
    }
    try {
      final json = await _send('GET', '/api/driver/gate-pass') as Map<String, dynamic>;
      await _store.write(_passKey, jsonEncode(json));
      return GatePass.fromJson(json);
    } on ApiException {
      rethrow;
    } catch (_) {
      return null; // offline: the static gate code is shown instead
    }
  }

  Future<TripUpdate> reportPosition(String appointmentNo, double latitude, double longitude, double? speed) async =>
      TripUpdate.fromJson(await _send('POST', '/api/driver/appointments/${Uri.encodeComponent(appointmentNo)}/position',
          body: {'latitude': latitude, 'longitude': longitude, 'speed': speed}) as Map<String, dynamic>);

  /// "Done here": the stop is confirmed and the terminal's systems are told.
  Future<void> completeStop(String appointmentNo, int sequence) async =>
      _send('POST', '/api/driver/appointments/${Uri.encodeComponent(appointmentNo)}/stops/$sequence/done');

  /// Where the terminal sends push messages for this phone; null stops them.
  Future<void> registerPushToken(String? token) async => _send('PUT', '/api/driver/push-token', body: {'token': token});

  Future<YardGuidance> yard(String appointmentNo, {double? latitude, double? longitude}) async {
    final query = latitude == null || longitude == null ? '' : '?lat=$latitude&lon=$longitude';
    return YardGuidance.fromJson(
        await _send('GET', '/api/driver/appointments/${Uri.encodeComponent(appointmentNo)}/yard$query') as Map<String, dynamic>);
  }

  Future<void> signOut() async {
    await tracker.stop();
    try {
      if (_token != null) await _send('POST', '/api/driver/logout');
    } catch (_) {
      // Signed out on this phone either way.
    }
    _token = null;
    await _store.write(_tokenKey, null);
    await _store.write(_passKey, null);
  }

  Future<dynamic> _send(String method, String path, {Map<String, dynamic>? body, bool auth = true}) async {
    final headers = <String, String>{'Accept': 'application/json', 'Accept-Language': language};
    if (body != null) headers['Content-Type'] = 'application/json';
    if (auth && _token != null) headers['Authorization'] = 'Bearer $_token';

    final request = http.Request(method, Uri.parse('$_base$path'))..headers.addAll(headers);
    if (body != null) request.body = jsonEncode(body);

    final response = await http.Response.fromStream(await _client.send(request).timeout(const Duration(seconds: 20)));
    final text = utf8.decode(response.bodyBytes);

    if (response.statusCode == 401 && auth) {
      _token = null;
      await _store.write(_tokenKey, null);
    }
    if (response.statusCode >= 400) {
      var message = 'Request failed (${response.statusCode}).';
      try {
        final problem = jsonDecode(text) as Map<String, dynamic>;
        message = (problem['detail'] as String?) ?? (problem['title'] as String?) ?? message;
      } catch (_) {
        // Not JSON.
      }
      throw ApiException(response.statusCode, message);
    }
    return text.isEmpty ? null : jsonDecode(text);
  }
}
