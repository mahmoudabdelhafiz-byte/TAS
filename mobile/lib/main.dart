import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';

import 'api.dart';
import 'brand.dart';
import 'demo.dart';
import 'push.dart';
import 'screens/appointments.dart';
import 'screens/login.dart';
import 'strings.dart';
import 'theme.dart';

void main() {
  WidgetsFlutterBinding.ensureInitialized();
  runApp(YardsyncsDriverApp(api: demoMode ? DriverApi(client: startDemo()) : DriverApi()));
}

/// App state: language and whether the driver is signed in.
class YardsyncsDriverApp extends StatefulWidget {
  const YardsyncsDriverApp({super.key, required this.api});

  final DriverApi api;

  @override
  State<YardsyncsDriverApp> createState() => _YardsyncsDriverAppState();
}

class _YardsyncsDriverAppState extends State<YardsyncsDriverApp> {
  String _lang = 'ar';
  bool? _signedIn; // null while loading
  late final PushService _push = PushService(widget.api);

  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _load() async {
    // Language packs from the server; the built-in languages if it doesn't answer quickly.
    await widget.api.loadLanguagePacks().timeout(const Duration(seconds: 5), onTimeout: () {});
    final brand = await widget.api.branding().timeout(const Duration(seconds: 5), onTimeout: () => null);
    if (brand != null) appBrand = brand;
    final lang = await widget.api.savedLanguage();
    final signedIn = await widget.api.restore();
    if (!mounted) return;
    setState(() {
      if (lang != null && appLanguages.contains(lang)) _lang = lang;
      _signedIn = signedIn;
    });
    if (signedIn) _push.start();
  }

  void _setLang(String lang) {
    setState(() => _lang = lang);
    widget.api.saveLanguage(lang);
  }

  @override
  Widget build(BuildContext context) {
    return MaterialApp(
      title: 'Yardsyncs mobile',
      debugShowCheckedModeBanner: false,
      theme: yardsyncsTheme(primary: appBrand.color),
      locale: Locale(_lang),
      supportedLocales: appLanguages.map(Locale.new),
      localizationsDelegates: const [
        GlobalMaterialLocalizations.delegate,
        GlobalWidgetsLocalizations.delegate,
        GlobalCupertinoLocalizations.delegate,
      ],
      home: switch (_signedIn) {
        null => const Scaffold(body: Center(child: CircularProgressIndicator())),
        false => LoginScreen(
            api: widget.api,
            lang: _lang,
            onLanguage: _setLang,
            onSignedIn: (_) {
              setState(() => _signedIn = true);
              _push.start();
            },
          ),
        true => AppointmentsScreen(
            api: widget.api,
            push: _push,
            lang: _lang,
            onLanguage: _setLang,
            onSignedOut: () => setState(() => _signedIn = false),
          ),
      },
    );
  }
}
