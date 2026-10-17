import 'dart:async';

import 'package:flutter/material.dart';

import '../api.dart';
import '../models.dart';
import '../strings.dart';
import '../theme.dart';
import 'common.dart';

/// Messages with the control room about one visit: ready-made ones with one tap, or a text.
class MessagesScreen extends StatefulWidget {
  const MessagesScreen({super.key, required this.api, required this.lang, required this.appointmentNo});

  final DriverApi api;
  final String lang;
  final String appointmentNo;

  @override
  State<MessagesScreen> createState() => _MessagesScreenState();
}

class _MessagesScreenState extends State<MessagesScreen> {
  List<ChatMessage>? _messages;
  List<QuickMessage> _quick = const [];
  String? _error;
  bool _sending = false;
  Timer? _timer;
  final _text = TextEditingController();

  String t(String key) => tr(widget.lang, key);

  @override
  void initState() {
    super.initState();
    _load();
    _loadQuick();
    // The control room may answer while the driver waits.
    _timer = Timer.periodic(const Duration(seconds: 20), (_) => _load());
  }

  @override
  void dispose() {
    _timer?.cancel();
    _text.dispose();
    super.dispose();
  }

  Future<void> _loadQuick() async {
    try {
      final quick = await widget.api.quickMessages();
      if (mounted) setState(() => _quick = quick);
    } catch (_) {
      // Offline: the driver can still write a text.
    }
  }

  Future<void> _load() async {
    try {
      final messages = await widget.api.messages(widget.appointmentNo);
      if (mounted) {
        setState(() {
          _messages = messages;
          _error = null;
        });
      }
    } on ApiException catch (e) {
      if (mounted) setState(() => _error = e.message);
    } catch (_) {
      if (mounted) setState(() => _error = t('offline'));
    }
  }

  Future<void> _send({String? templateCode}) async {
    final text = _text.text.trim();
    if (templateCode == null && text.isEmpty) return;
    setState(() {
      _sending = true;
      _error = null;
    });
    try {
      await widget.api.sendMessage(widget.appointmentNo, templateCode: templateCode, text: templateCode == null ? text : null);
      if (templateCode == null) _text.clear();
      await _load();
    } on ApiException catch (e) {
      if (mounted) setState(() => _error = e.message);
    } catch (_) {
      if (mounted) setState(() => _error = t('offline'));
    } finally {
      if (mounted) setState(() => _sending = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final messages = _messages;
    return Scaffold(
      appBar: AppBar(title: Text(t('messages'))),
      body: Column(children: [
        Expanded(
          child: RefreshIndicator(
            onRefresh: _load,
            child: ListView(
              reverse: true,
              padding: const EdgeInsets.all(16),
              children: [
                for (final m in (messages ?? const <ChatMessage>[]).reversed) _Bubble(message: m, lang: widget.lang),
                if (messages != null && messages.isEmpty)
                  Padding(
                    padding: const EdgeInsets.symmetric(vertical: 32),
                    child: Text(t('noMessages'), style: const TextStyle(fontSize: 16, color: muted)),
                  ),
                if (messages == null && _error == null) const Padding(padding: EdgeInsets.all(32), child: Center(child: CircularProgressIndicator())),
                Padding(
                  padding: const EdgeInsets.only(bottom: 12),
                  child: Text(t('messagesLead'), style: const TextStyle(fontSize: 15, color: muted)),
                ),
              ],
            ),
          ),
        ),
        if (_error != null) Padding(padding: const EdgeInsets.symmetric(horizontal: 16), child: ErrorBox(message: _error!)),
        SafeArea(
          top: false,
          child: Container(
            decoration: const BoxDecoration(color: Colors.white, border: Border(top: BorderSide(color: line))),
            padding: const EdgeInsets.fromLTRB(12, 10, 12, 10),
            child: Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
              if (_quick.isNotEmpty) ...[
                Text(t('quickMessages'), style: const TextStyle(fontSize: 13, color: muted)),
                const SizedBox(height: 6),
                Wrap(spacing: 8, runSpacing: 8, children: [
                  for (final q in _quick)
                    ActionChip(
                      label: Text(q.text, style: const TextStyle(fontSize: 15)),
                      onPressed: _sending ? null : () => _send(templateCode: q.code),
                    ),
                ]),
                const SizedBox(height: 10),
              ],
              Row(children: [
                Expanded(
                  child: TextField(
                    controller: _text,
                    maxLength: 500,
                    minLines: 1,
                    maxLines: 4,
                    textInputAction: TextInputAction.send,
                    onSubmitted: (_) => _send(),
                    decoration: InputDecoration(hintText: t('writeMessage'), counterText: '', border: const OutlineInputBorder()),
                  ),
                ),
                const SizedBox(width: 8),
                IconButton.filled(
                  tooltip: t('send'),
                  onPressed: _sending ? null : () => _send(),
                  icon: const Icon(Icons.send),
                ),
              ]),
            ]),
          ),
        ),
      ]),
    );
  }
}

class _Bubble extends StatelessWidget {
  const _Bubble({required this.message, required this.lang});

  final ChatMessage message;
  final String lang;

  @override
  Widget build(BuildContext context) {
    final mine = message.fromDriver;
    final local = message.sentAt.toLocal();
    final time = '${two(local.hour)}:${two(local.minute)}';
    return Align(
      alignment: mine ? AlignmentDirectional.centerEnd : AlignmentDirectional.centerStart,
      child: Container(
        constraints: const BoxConstraints(maxWidth: 320),
        margin: const EdgeInsets.only(bottom: 10),
        padding: const EdgeInsets.fromLTRB(12, 8, 12, 8),
        decoration: BoxDecoration(
          color: mine ? teal.withValues(alpha: 0.12) : Colors.white,
          border: Border.all(color: mine ? teal.withValues(alpha: 0.3) : line),
          borderRadius: BorderRadius.circular(8),
        ),
        child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
          Text(tr(lang, mine ? 'you' : 'controlRoom'), style: const TextStyle(fontSize: 13, color: muted)),
          const SizedBox(height: 2),
          Text(message.text, style: const TextStyle(fontSize: 17, color: ink, height: 1.3)),
          Align(
            alignment: AlignmentDirectional.centerEnd,
            child: Text(time, textDirection: TextDirection.ltr, style: const TextStyle(fontSize: 12, color: muted)),
          ),
        ]),
      ),
    );
  }
}
