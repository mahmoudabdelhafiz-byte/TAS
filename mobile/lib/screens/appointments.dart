import 'dart:async';

import 'package:flutter/material.dart';
import 'package:qr_flutter/qr_flutter.dart';

import '../api.dart';
import '../gate_pass.dart';
import '../models.dart';
import '../push.dart';
import '../strings.dart';
import '../theme.dart';
import 'common.dart';
import 'yard_screen.dart';

/// My appointments: the next ones first, each with its traffic light.
class AppointmentsScreen extends StatefulWidget {
  const AppointmentsScreen({super.key, required this.api, required this.lang, required this.onLanguage, required this.onSignedOut, this.push});

  final DriverApi api;
  final String lang;
  final ValueChanged<String> onLanguage;
  final VoidCallback onSignedOut;

  /// Messages from the terminal (null in tests and when push isn't set up).
  final PushService? push;

  @override
  State<AppointmentsScreen> createState() => _AppointmentsScreenState();
}

class _AppointmentsScreenState extends State<AppointmentsScreen> {
  List<DriverAppointment>? _items;
  String? _error;
  Timer? _timer;
  StreamSubscription<PushMessage>? _pushes;

  String t(String key) => tr(widget.lang, key);

  @override
  void initState() {
    super.initState();
    _load();
    _timer = Timer.periodic(const Duration(seconds: 60), (_) => _load());
    _pushes = widget.push?.messages.listen(_onPush);
  }

  @override
  void dispose() {
    _timer?.cancel();
    _pushes?.cancel();
    super.dispose();
  }

  /// A message from the terminal: refresh; at gate-in go straight to the ticket and directions.
  Future<void> _onPush(PushMessage m) async {
    await _load();
    if (!mounted || !m.isGateIn || m.appointmentNo.isEmpty) {
      if (mounted && m.body != null) {
        ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(m.body!), duration: const Duration(seconds: 8)));
      }
      return;
    }
    if (m.opened) {
      _openYard(m.appointmentNo);
    } else {
      ScaffoldMessenger.of(context).showSnackBar(SnackBar(
        content: Text(m.body ?? t('whereToGo')),
        duration: const Duration(seconds: 20),
        action: SnackBarAction(label: t('whereToGo'), onPressed: () => _openYard(m.appointmentNo)),
      ));
    }
  }

  void _openYard(String appointmentNo) {
    Navigator.of(context).push(MaterialPageRoute<void>(
      builder: (_) => YardScreen(api: widget.api, lang: widget.lang, appointmentNo: appointmentNo),
    ));
  }

  Future<void> _load() async {
    try {
      final items = await widget.api.appointments();
      // A trip on the way keeps sharing its position, also after the app was closed and opened again.
      final onTheWay = items.where((a) => a.status == 'EnRoute').toList();
      if (onTheWay.isNotEmpty) {
        unawaited(widget.api.tracker.follow(onTheWay.first.appointmentNo, widget.lang));
      } else if (widget.api.tracker.appointmentNo != null) {
        unawaited(widget.api.tracker.stop());
      }
      if (!mounted) return;
      setState(() {
        _items = items;
        _error = null;
      });
    } on ApiException catch (e) {
      if (e.signedOut) {
        widget.onSignedOut();
        return;
      }
      if (mounted) setState(() => _error = e.message);
    } catch (_) {
      if (mounted) setState(() => _error = t('offline'));
    }
  }

  Future<void> _signOut() async {
    await widget.api.signOut();
    widget.onSignedOut();
  }

  @override
  Widget build(BuildContext context) {
    final items = _items;
    return Scaffold(
      appBar: AppBar(
        title: Text(t('myTrips')),
        actions: [
          LanguageButton(lang: widget.lang, onLanguage: widget.onLanguage, color: Colors.white),
          IconButton(tooltip: t('signOut'), icon: const Icon(Icons.logout), onPressed: _signOut),
        ],
      ),
      body: RefreshIndicator(
        onRefresh: _load,
        child: ListView(
          padding: const EdgeInsets.all(16),
          children: [
            if (_error != null) ...[ErrorBox(message: _error!), const SizedBox(height: 12)],
            if (items == null && _error == null) const Padding(padding: EdgeInsets.all(48), child: Center(child: CircularProgressIndicator())),
            if (items != null && items.isEmpty)
              Padding(padding: const EdgeInsets.symmetric(vertical: 48), child: Text(t('noTrips'), style: const TextStyle(fontSize: 17, color: muted))),
            for (final a in items ?? const <DriverAppointment>[]) ...[
              _AppointmentCard(
                appointment: a,
                lang: widget.lang,
                onOpen: () async {
                  await Navigator.of(context).push(MaterialPageRoute<void>(
                    builder: (_) => AppointmentScreen(api: widget.api, lang: widget.lang, appointment: a, onSignedOut: widget.onSignedOut),
                  ));
                  _load();
                },
              ),
              const SizedBox(height: 12),
            ],
          ],
        ),
      ),
    );
  }
}

class _AppointmentCard extends StatelessWidget {
  const _AppointmentCard({required this.appointment, required this.lang, required this.onOpen});

  final DriverAppointment appointment;
  final String lang;
  final VoidCallback onOpen;

  @override
  Widget build(BuildContext context) {
    final a = appointment;
    final word = dayWord(a, DateTime.now().toUtc());
    return Card(
      clipBehavior: Clip.antiAlias,
      child: InkWell(
        onTap: onOpen,
        child: Container(
          decoration: BoxDecoration(border: BorderDirectional(start: BorderSide(color: a.isActive ? lightColor(a.trafficLight) : line, width: 6))),
          padding: const EdgeInsets.fromLTRB(16, 14, 16, 14),
          child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
            Row(children: [
              Text(word == null ? shortDate(a) : tr(lang, word), style: const TextStyle(fontSize: 15, color: muted)),
              const Spacer(),
              Text(tr(lang, 'status.${a.status}'), style: const TextStyle(fontSize: 14, color: muted)),
            ]),
            const SizedBox(height: 2),
            Text(hours(a), textDirection: TextDirection.ltr, style: const TextStyle(fontSize: 30, fontWeight: FontWeight.w600, color: ink)),
            Text(a.terminalName, style: const TextStyle(fontSize: 16, color: ink)),
            const SizedBox(height: 8),
            if (a.isActive) LightBadge(light: a.trafficLight, lang: lang),
            const SizedBox(height: 6),
            Wrap(spacing: 8, runSpacing: 6, children: [
              for (final m in a.moves)
                Container(
                  padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 3),
                  decoration: BoxDecoration(border: Border.all(color: line), borderRadius: BorderRadius.circular(3)),
                  child: Text(m.reference, textDirection: TextDirection.ltr, style: const TextStyle(fontSize: 15, letterSpacing: 0.5)),
                ),
            ]),
          ]),
        ),
      ),
    );
  }
}

/// One appointment: what to bring, the gate code and "I am leaving now".
class AppointmentScreen extends StatefulWidget {
  const AppointmentScreen({super.key, required this.api, required this.lang, required this.appointment, required this.onSignedOut});

  final DriverApi api;
  final String lang;
  final DriverAppointment appointment;
  final VoidCallback onSignedOut;

  @override
  State<AppointmentScreen> createState() => _AppointmentScreenState();
}

class _AppointmentScreenState extends State<AppointmentScreen> {
  late DriverAppointment _a = widget.appointment;
  bool _busy = false;
  String? _error;
  String? _done;
  GatePass? _pass;
  Timer? _tick;
  String? _locationProblem;

  String t(String key, [Map<String, String> values = const {}]) => tr(widget.lang, key, values);

  @override
  void initState() {
    super.initState();
    _loadPass();
    // Redraw every second: the gate code rotates and the countdown moves.
    _tick = Timer.periodic(const Duration(seconds: 1), (_) {
      if (mounted && _a.gateCode != null) setState(() {});
    });
    widget.api.tracker.addListener(_onTracking);
    if (_a.status == 'EnRoute') _startTracking();
  }

  @override
  void dispose() {
    _tick?.cancel();
    widget.api.tracker.removeListener(_onTracking);
    super.dispose();
  }

  Future<void> _loadPass() async {
    try {
      final pass = await widget.api.gatePass();
      if (mounted) setState(() => _pass = pass);
    } catch (_) {
      // Signed out or offline: the static gate code is shown.
    }
  }

  /// On the way: the position is shared in the background until the truck checks in at the pre-gate.
  void _startTracking() => unawaited(widget.api.tracker.follow(_a.appointmentNo, widget.lang));

  void _onTracking() {
    final tracker = widget.api.tracker;
    if (!mounted) return;
    setState(() {
      _locationProblem = tracker.problem == null ? null : t('locationOff');
      final update = tracker.last;
      if (update != null && tracker.lastAppointmentNo == _a.appointmentNo) {
        _a = _a.copyWith(status: update.status, eta: update.eta);
        if (update.checkedIn) _done = t('checkedIn');
      }
    });
  }

  Future<void> _startTrip() async {
    setState(() {
      _busy = true;
      _error = null;
    });
    try {
      final updated = await widget.api.startTrip(_a.appointmentNo);
      setState(() {
        _a = updated;
        _done = t('tripStarted');
      });
      if (updated.status == 'EnRoute') _startTracking();
    } on ApiException catch (e) {
      if (e.signedOut) {
        if (mounted) Navigator.of(context).pop();
        widget.onSignedOut();
        return;
      }
      setState(() => _error = e.message);
    } catch (_) {
      setState(() => _error = t('offline'));
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final a = _a;
    final word = dayWord(a, DateTime.now().toUtc());
    return Scaffold(
      appBar: AppBar(title: Text(a.appointmentNo, textDirection: TextDirection.ltr)),
      body: ListView(
        padding: const EdgeInsets.all(20),
        children: [
          Wrap(spacing: 12, children: [
            Text(word == null ? shortDate(a) : tr(widget.lang, word), style: const TextStyle(fontSize: 16, color: muted)),
            Text(a.terminalName, style: const TextStyle(fontSize: 16, color: muted)),
          ]),
          Text(hours(a), textDirection: TextDirection.ltr, style: const TextStyle(fontSize: 40, fontWeight: FontWeight.w600, color: ink)),
          const SizedBox(height: 12),
          if (a.isActive)
            Container(
              padding: const EdgeInsets.all(14),
              decoration: BoxDecoration(color: lightColor(a.trafficLight).withValues(alpha: 0.12), borderRadius: BorderRadius.circular(6)),
              child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                LightBadge(light: a.trafficLight, lang: widget.lang, large: true),
                if (a.trafficReason.isNotEmpty) ...[
                  const SizedBox(height: 6),
                  Text(a.trafficReason, style: const TextStyle(fontSize: 16, color: ink)),
                ],
              ]),
            )
          else
            Text(t('status.${a.status}'), style: const TextStyle(fontSize: 20, fontWeight: FontWeight.w600, color: ink)),
          const SizedBox(height: 20),
          Text(t('moves'), style: const TextStyle(fontSize: 15, color: muted)),
          for (final m in a.moves)
            Padding(
              padding: const EdgeInsets.symmetric(vertical: 6),
              child: Row(children: [
                Text(m.reference, textDirection: TextDirection.ltr, style: const TextStyle(fontSize: 20, fontWeight: FontWeight.w600, letterSpacing: 0.5)),
                const SizedBox(width: 12),
                Expanded(child: Text(m.name(widget.lang), style: const TextStyle(fontSize: 15, color: muted))),
              ]),
            ),
          const SizedBox(height: 8),
          _Fact(label: t('company'), value: a.companyName),
          if (a.truckPlate != null) _Fact(label: t('truck'), value: a.truckPlate!, ltr: true),
          if (a.status == 'EnRoute') ...[
            const SizedBox(height: 16),
            if (a.etaClock() != null)
              Text(t('eta', {'time': a.etaClock()!}), style: const TextStyle(fontSize: 18, fontWeight: FontWeight.w600, color: ink)),
            Text(_locationProblem ?? t('sharing'), style: TextStyle(fontSize: 14, color: _locationProblem == null ? muted : red)),
          ],
          if (a.gateCode != null) ...[
            const SizedBox(height: 20),
            Text(t('gateCode'), style: const TextStyle(fontSize: 16, color: ink, fontWeight: FontWeight.w600)),
            const SizedBox(height: 10),
            Center(
              child: Container(
                color: Colors.white,
                padding: const EdgeInsets.all(12),
                child: QrImageView(
                  data: _pass == null ? a.gateCode! : _pass!.payload(a.appointmentNo, DateTime.now()),
                  size: 240,
                  backgroundColor: Colors.white,
                ),
              ),
            ),
            if (_pass != null)
              Center(
                child: Text(t('codeChanges', {'s': '${_pass!.secondsLeft(DateTime.now())}'}),
                    style: const TextStyle(fontSize: 14, color: muted)),
              ),
          ],
          const SizedBox(height: 24),
          if (_error != null) ...[ErrorBox(message: _error!), const SizedBox(height: 12)],
          if (_done != null) ...[
            Text(_done!, style: const TextStyle(fontSize: 16, color: green, fontWeight: FontWeight.w600)),
            const SizedBox(height: 12),
          ],
          if (a.status == 'GateIn') ...[
            FilledButton(
              onPressed: () => Navigator.of(context).push(MaterialPageRoute<void>(
                builder: (_) => YardScreen(api: widget.api, lang: widget.lang, appointmentNo: a.appointmentNo),
              )),
              child: Text(t('whereToGo')),
            ),
            const SizedBox(height: 12),
          ],
          if (a.status == 'Confirmed') ...[
            FilledButton(onPressed: _busy ? null : _startTrip, child: Text(t('startTrip'))),
            const SizedBox(height: 8),
            Text(t('startTripHint'), style: const TextStyle(fontSize: 14, color: muted)),
          ],
        ],
      ),
    );
  }
}

class _Fact extends StatelessWidget {
  const _Fact({required this.label, required this.value, this.ltr = false});

  final String label;
  final String value;
  final bool ltr;

  @override
  Widget build(BuildContext context) => Padding(
        padding: const EdgeInsets.symmetric(vertical: 4),
        child: Row(children: [
          SizedBox(width: 110, child: Text(label, style: const TextStyle(fontSize: 15, color: muted))),
          Expanded(child: Text(value, textDirection: ltr ? TextDirection.ltr : null, style: const TextStyle(fontSize: 17, color: ink))),
        ]),
      );
}
