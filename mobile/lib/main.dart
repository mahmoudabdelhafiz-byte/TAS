import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';

import 'api.dart';
import 'screens/appointments.dart';
import 'screens/login.dart';
import 'strings.dart';
import 'theme.dart';

void main() {
  WidgetsFlutterBinding.ensureInitialized();
  runApp(YardsyncsDriverApp(api: DriverApi()));
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

  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _load() async {
    final lang = await widget.api.savedLanguage();
    final signedIn = await widget.api.restore();
    if (!mounted) return;
    setState(() {
      if (lang != null && supportedLanguages.contains(lang)) _lang = lang;
      _signedIn = signedIn;
    });
  }

  void _setLang(String lang) {
    setState(() => _lang = lang);
    widget.api.saveLanguage(lang);
  }

  @override
  Widget build(BuildContext context) {
    return MaterialApp(
      title: 'Yardsyncs',
      debugShowCheckedModeBanner: false,
      theme: yardsyncsTheme(),
      locale: Locale(_lang),
      supportedLocales: supportedLanguages.map(Locale.new),
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
            onSignedIn: (_) => setState(() => _signedIn = true),
          ),
        true => AppointmentsScreen(
            api: widget.api,
            lang: _lang,
            onLanguage: _setLang,
            onSignedOut: () => setState(() => _signedIn = false),
          ),
      },
    );
  }
}
