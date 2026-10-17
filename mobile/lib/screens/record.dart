import 'package:flutter/material.dart';

import '../api.dart';
import '../models.dart';
import '../strings.dart';
import '../theme.dart';
import 'common.dart';

/// "dd/mm/yyyy" on the phone's clock.
String dayOf(DateTime utc) {
  final l = utc.toLocal();
  return '${two(l.day)}/${two(l.month)}/${l.year}';
}

/// The driver's record: points per terminal and each entry; an entry can be disputed once.
class RecordScreen extends StatefulWidget {
  const RecordScreen({super.key, required this.api, required this.lang});

  final DriverApi api;
  final String lang;

  @override
  State<RecordScreen> createState() => _RecordScreenState();
}

class _RecordScreenState extends State<RecordScreen> {
  MyRecord? _record;
  String? _error;
  String? _done;

  String t(String key, [Map<String, String> values = const {}]) => tr(widget.lang, key, values);

  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _load() async {
    try {
      final record = await widget.api.myRecord();
      if (mounted) {
        setState(() {
          _record = record;
          _error = null;
        });
      }
    } on ApiException catch (e) {
      if (mounted) setState(() => _error = e.message);
    } catch (_) {
      if (mounted) setState(() => _error = t('offline'));
    }
  }

  Future<void> _dispute(RecordEntry entry) async {
    final controller = TextEditingController();
    final text = await showDialog<String>(
      context: context,
      builder: (context) => AlertDialog(
        title: Text(t('disputeTitle')),
        content: TextField(controller: controller, maxLength: 500, minLines: 2, maxLines: 5, autofocus: true),
        actions: [
          TextButton(onPressed: () => Navigator.of(context).pop(), child: Text(t('notNow'))),
          FilledButton(onPressed: () => Navigator.of(context).pop(controller.text.trim()), child: Text(t('send'))),
        ],
      ),
    );
    controller.dispose();
    if (text == null || text.isEmpty) return;
    try {
      final record = await widget.api.disputeEntry(entry.id, text);
      if (mounted) {
        setState(() {
          _record = record;
          _done = t('disputeSent');
          _error = null;
        });
      }
    } on ApiException catch (e) {
      if (mounted) setState(() => _error = e.message);
    } catch (_) {
      if (mounted) setState(() => _error = t('offline'));
    }
  }

  @override
  Widget build(BuildContext context) {
    final r = _record;
    return Scaffold(
      appBar: AppBar(title: Text(t('myRecord'))),
      body: RefreshIndicator(
        onRefresh: _load,
        child: ListView(
          padding: const EdgeInsets.all(16),
          children: [
            Text(t('recordLead'), style: const TextStyle(fontSize: 15, color: muted)),
            const SizedBox(height: 12),
            if (_error != null) ...[ErrorBox(message: _error!), const SizedBox(height: 12)],
            if (_done != null) ...[
              Text(_done!, style: const TextStyle(fontSize: 15, color: green, fontWeight: FontWeight.w600)),
              const SizedBox(height: 12),
            ],
            if (r == null && _error == null) const Padding(padding: EdgeInsets.all(32), child: Center(child: CircularProgressIndicator())),
            if (r != null && r.entries.isEmpty) Text(t('noRecord'), style: const TextStyle(fontSize: 17, color: ink)),
            if (r != null)
              for (final p in r.terminals)
                Card(
                  child: ListTile(
                    title: Text(p.terminalName),
                    subtitle: Text(t('standing.${p.standing}')),
                    trailing: Text(t('pointsAt', {'n': '${p.points}'}),
                        style: TextStyle(fontSize: 20, fontWeight: FontWeight.w700, color: p.standing == 'Clear' ? ink : (p.standing == 'Warning' ? amber : red))),
                  ),
                ),
            const SizedBox(height: 8),
            if (r != null) for (final e in r.entries) _Entry(entry: e, t: t, onDispute: () => _dispute(e)),
          ],
        ),
      ),
    );
  }
}

class _Entry extends StatelessWidget {
  const _Entry({required this.entry, required this.t, required this.onDispute});

  final RecordEntry entry;
  final String Function(String, [Map<String, String>]) t;
  final VoidCallback onDispute;

  @override
  Widget build(BuildContext context) {
    final e = entry;
    final status = e.withdrawn
        ? t('withdrawnEntry')
        : switch (e.disputeStatus) {
            'Open' => t('disputeOpen'),
            'Accepted' => t('disputeAccepted'),
            'Rejected' => t('disputeRejected'),
            _ => e.counts ? t('countsUntil', {'date': dayOf(e.countsUntil)}) : t('notCounting'),
          };
    return Opacity(
      opacity: e.counts ? 1 : 0.65,
      child: Container(
        margin: const EdgeInsets.only(bottom: 10),
        padding: const EdgeInsets.all(14),
        decoration: BoxDecoration(color: Colors.white, border: Border.all(color: line), borderRadius: BorderRadius.circular(6)),
        child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
          Row(children: [
            Expanded(child: Text(t('incident.${e.kind}'), style: const TextStyle(fontSize: 18, fontWeight: FontWeight.w600, color: ink))),
            Text(t('pointsAt', {'n': '${e.points}'}), style: const TextStyle(fontSize: 16, fontWeight: FontWeight.w700, color: ink)),
          ]),
          const SizedBox(height: 4),
          Wrap(spacing: 10, children: [
            Text(dayOf(e.occurredAt), textDirection: TextDirection.ltr, style: const TextStyle(color: muted)),
            Text(e.terminalName, style: const TextStyle(color: muted)),
            if (e.appointmentNo != null) Text(e.appointmentNo!, textDirection: TextDirection.ltr, style: const TextStyle(color: muted)),
          ]),
          if (e.note != null) ...[const SizedBox(height: 6), Text(e.note!, style: const TextStyle(fontSize: 15, color: ink))],
          const SizedBox(height: 6),
          Text(status, style: const TextStyle(fontSize: 14, color: muted, fontWeight: FontWeight.w600)),
          if (e.disputeText != null) Text('“${e.disputeText!}”', style: const TextStyle(fontSize: 14, color: ink)),
          if (e.disputeAnswer != null) Text(t('terminalAnswer', {'text': e.disputeAnswer!}), style: const TextStyle(fontSize: 14, color: ink)),
          if (e.canDispute) ...[
            const SizedBox(height: 8),
            Align(alignment: AlignmentDirectional.centerStart, child: OutlinedButton(onPressed: onDispute, child: Text(t('dispute')))),
          ],
        ]),
      ),
    );
  }
}
