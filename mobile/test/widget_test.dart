import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:yardsyncs_driver/api.dart';
import 'package:yardsyncs_driver/screens/login.dart';

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
}
