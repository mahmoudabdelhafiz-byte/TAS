import 'package:flutter/material.dart';

import '../api.dart';
import '../models.dart';
import '../strings.dart';
import '../theme.dart';
import 'common.dart';
import 'record.dart';

/// A terminal's safety induction: the pages, then the questions; every answer right passes it.
class InductionScreen extends StatefulWidget {
  const InductionScreen({super.key, required this.api, required this.lang, required this.course});

  final DriverApi api;
  final String lang;
  final InductionCourse course;

  @override
  State<InductionScreen> createState() => _InductionScreenState();
}

class _InductionScreenState extends State<InductionScreen> {
  late final List<int?> _answers = List<int?>.filled(widget.course.questions.length, null);
  List<int> _wrong = const [];
  String? _error;
  String? _result;
  bool _passed = false;
  bool _sending = false;

  String t(String key, [Map<String, String> values = const {}]) => tr(widget.lang, key, values);

  Future<void> _check() async {
    setState(() {
      _sending = true;
      _error = null;
    });
    try {
      final result = await widget.api.answerInduction(widget.course.terminalId, _answers.map((a) => a ?? -1).toList());
      setState(() {
        _passed = result.passed;
        _wrong = result.wrong;
        _result = result.passed
            ? t('inductionPassed', {'date': result.validUntil == null ? '' : dayOf(result.validUntil!)})
            : t('inductionWrong', {'n': '${result.wrong.length}'});
      });
    } on ApiException catch (e) {
      setState(() => _error = e.message);
    } catch (_) {
      setState(() => _error = t('offline'));
    } finally {
      if (mounted) setState(() => _sending = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final c = widget.course;
    return Scaffold(
      appBar: AppBar(title: Text(t('inductionNeeded', {'terminal': c.terminalName}))),
      body: ListView(
        padding: const EdgeInsets.all(16),
        children: [
          Text(t('inductionLead'), style: const TextStyle(fontSize: 15, color: muted)),
          const SizedBox(height: 12),
          for (final (title, body) in c.pages)
            Card(
              child: Padding(
                padding: const EdgeInsets.all(14),
                child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                  Text(title, style: const TextStyle(fontSize: 18, fontWeight: FontWeight.w600, color: ink)),
                  const SizedBox(height: 6),
                  Text(body, style: const TextStyle(fontSize: 16, color: ink, height: 1.4)),
                ]),
              ),
            ),
          const SizedBox(height: 16),
          Text(t('questionsTitle'), style: const TextStyle(fontSize: 18, fontWeight: FontWeight.w700, color: ink)),
          for (var i = 0; i < c.questions.length; i++) ...[
            const SizedBox(height: 10),
            Text('${i + 1}. ${c.questions[i].$1}',
                style: TextStyle(fontSize: 16, fontWeight: FontWeight.w600, color: _wrong.contains(i) ? red : ink)),
            RadioGroup<int>(
              groupValue: _answers[i],
              onChanged: _passed ? (_) {} : (v) => setState(() => _answers[i] = v),
              child: Column(children: [
                for (var o = 0; o < c.questions[i].$2.length; o++)
                  RadioListTile<int>(value: o, title: Text(c.questions[i].$2[o]), contentPadding: EdgeInsets.zero, dense: true),
              ]),
            ),
          ],
          const SizedBox(height: 16),
          if (_error != null) ...[ErrorBox(message: _error!), const SizedBox(height: 12)],
          if (_result != null) ...[
            Text(_result!, style: TextStyle(fontSize: 16, fontWeight: FontWeight.w600, color: _passed ? green : red)),
            const SizedBox(height: 12),
          ],
          if (!_passed)
            FilledButton(
              onPressed: _sending || _answers.any((a) => a == null) ? null : _check,
              child: Text(t('checkAnswers')),
            )
          else
            FilledButton(onPressed: () => Navigator.of(context).pop(true), child: Text(t('ok'))),
        ],
      ),
    );
  }
}
