import 'package:flutter/material.dart';

import '../api.dart';
import '../models.dart';
import '../strings.dart';
import '../theme.dart';
import 'common.dart';

/// Mobile number → code by SMS → signed in.
class LoginScreen extends StatefulWidget {
  const LoginScreen({super.key, required this.api, required this.lang, required this.onLanguage, required this.onSignedIn});

  final DriverApi api;
  final String lang;
  final ValueChanged<String> onLanguage;
  final ValueChanged<DriverProfile> onSignedIn;

  @override
  State<LoginScreen> createState() => _LoginScreenState();
}

class _LoginScreenState extends State<LoginScreen> {
  final _mobile = TextEditingController();
  final _code = TextEditingController();
  bool _codeSent = false;
  String _mobileExample = '';
  bool _busy = false;
  String? _error;

  String t(String key, [Map<String, String> values = const {}]) => tr(widget.lang, key, values);

  @override
  void initState() {
    super.initState();
    widget.api.mobileExample().then((example) {
      if (mounted && example != null) setState(() => _mobileExample = example);
    });
  }

  @override
  void dispose() {
    _mobile.dispose();
    _code.dispose();
    super.dispose();
  }

  Future<void> _run(Future<void> Function() action) async {
    setState(() {
      _busy = true;
      _error = null;
    });
    try {
      await action();
    } on ApiException catch (e) {
      setState(() => _error = e.message);
    } catch (_) {
      setState(() => _error = t('offline'));
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<void> _sendCode() => _run(() async {
        await widget.api.requestCode(_mobile.text.trim());
        setState(() => _codeSent = true);
      });

  Future<void> _verify() => _run(() async {
        final profile = await widget.api.verify(_mobile.text.trim(), _code.text.trim());
        widget.onSignedIn(profile);
      });

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      body: SafeArea(
        child: ListView(
          padding: const EdgeInsets.fromLTRB(24, 16, 24, 32),
          children: [
            Row(
              children: [
                const BrandMark(),
                const SizedBox(width: 10),
                Text(t('appName'), style: const TextStyle(fontSize: 20, fontWeight: FontWeight.w600, color: ink)),
                const Spacer(),
                LanguageButton(lang: widget.lang, onLanguage: widget.onLanguage),
              ],
            ),
            const SizedBox(height: 48),
            Text(t('signInTitle'), style: const TextStyle(fontSize: 28, fontWeight: FontWeight.w600, color: ink)),
            const SizedBox(height: 8),
            Text(_codeSent ? t('codeSent', {'mobile': _mobile.text.trim()}) : t('signInLead'),
                style: const TextStyle(fontSize: 16, color: muted)),
            const SizedBox(height: 28),
            if (!_codeSent) ...[
              TextField(
                controller: _mobile,
                keyboardType: TextInputType.phone,
                textDirection: TextDirection.ltr,
                autofillHints: const [AutofillHints.telephoneNumber],
                style: const TextStyle(fontSize: 22, letterSpacing: 1),
                decoration: InputDecoration(labelText: t('mobile'), hintText: _mobileExample),
              ),
              const SizedBox(height: 20),
              FilledButton(onPressed: _busy ? null : _sendCode, child: Text(t('sendCode'))),
            ] else ...[
              TextField(
                controller: _code,
                keyboardType: TextInputType.number,
                textDirection: TextDirection.ltr,
                autofillHints: const [AutofillHints.oneTimeCode],
                maxLength: 6,
                textAlign: TextAlign.center,
                style: const TextStyle(fontSize: 30, letterSpacing: 8, fontWeight: FontWeight.w600),
                decoration: InputDecoration(labelText: t('code'), counterText: ''),
              ),
              const SizedBox(height: 20),
              FilledButton(onPressed: _busy ? null : _verify, child: Text(t('confirm'))),
              const SizedBox(height: 8),
              Row(
                mainAxisAlignment: MainAxisAlignment.spaceBetween,
                children: [
                  TextButton(onPressed: _busy ? null : () => setState(() => _codeSent = false), child: Text(t('changeNumber'))),
                  TextButton(onPressed: _busy ? null : _sendCode, child: Text(t('resend'))),
                ],
              ),
            ],
            if (_error != null) ...[
              const SizedBox(height: 16),
              ErrorBox(message: _error!),
            ],
          ],
        ),
      ),
    );
  }
}
