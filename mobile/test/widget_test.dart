import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:yardsyncs_mobile/api.dart';
import 'package:yardsyncs_mobile/screens/login.dart';

class MemoryStore implements SessionStore {
  final values = <String, String>{};

  @override
  Future<String?> read(String key) async => values[key];

  @override
  Future<void> write(String key, String? value) async {
    if (value == null) {
      values.remove(key);
    } else {
      values[key] = value;
    }
  }
}

Widget app(String lang) => MaterialApp(
      locale: Locale(lang),
      supportedLocales: const [Locale('ar'), Locale('en'), Locale('ur'), Locale('hi')],
      localizationsDelegates: const [GlobalMaterialLocalizations.delegate, GlobalWidgetsLocalizations.delegate, GlobalCupertinoLocalizations.delegate],
      home: LoginScreen(api: DriverApi(store: MemoryStore()), lang: lang, onLanguage: (_) {}, onSignedIn: (_) {}),
    );

void main() {
  testWidgets('sign-in screen in Arabic is right-to-left', (tester) async {
    await tester.pumpWidget(app('ar'));
    expect(find.text('تسجيل الدخول'), findsOneWidget);
    expect(find.text('أرسل الرمز'), findsOneWidget);
    expect(Directionality.of(tester.element(find.text('تسجيل الدخول'))), TextDirection.rtl);
  });

  testWidgets('sign-in screen in English', (tester) async {
    await tester.pumpWidget(app('en'));
    expect(find.text('Send code'), findsOneWidget);
    expect(Directionality.of(tester.element(find.text('Send code'))), TextDirection.ltr);
  });

  test('the location notice is asked once', () async {
    final store = MemoryStore();
    final api = DriverApi(store: store);
    expect(await api.locationNoticeAccepted(), isFalse);
    await api.acceptLocationNotice();
    expect(await DriverApi(store: store).locationNoticeAccepted(), isTrue);
  });

  test('requests carry the app language so reasons come back in it', () async {
    final seen = <String?>[];
    final store = MemoryStore()..values['language'] = 'ur';
    final api = DriverApi(
      store: store,
      client: MockClient((request) async {
        seen.add(request.headers['Accept-Language']);
        return http.Response('{"error":"x"}', 500);
      }),
    );
    expect(await api.savedLanguage(), 'ur');
    await api.branding();
    await api.saveLanguage('hi');
    await api.branding();
    expect(seen, ['ur', 'hi']);
  });
}
