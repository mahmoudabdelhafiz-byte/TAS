import 'dart:async';

import 'package:flutter/material.dart';
import 'package:geolocator/geolocator.dart';

import '../api.dart';
import '../location.dart';
import '../strings.dart';
import '../theme.dart';
import '../voice.dart';
import '../yard.dart';
import 'common.dart';

/// Inside the terminal: the next stop in large text and the yard map with the route. The map is drawn on the phone
/// from the terminal's own plan, so it needs no map service and works with weak signal.
class YardScreen extends StatefulWidget {
  const YardScreen({super.key, required this.api, required this.lang, required this.appointmentNo});

  final DriverApi api;
  final String lang;
  final String appointmentNo;

  @override
  State<YardScreen> createState() => _YardScreenState();
}

class _YardScreenState extends State<YardScreen> {
  YardGuidance? _guidance;
  String? _error;
  int _current = 0;
  List<double>? _truck; // [lon, lat]
  Timer? _timer;
  Timer? _tick;
  final _voice = Voice();
  bool _voiceOn = true;
  String? _spokenKey;
  Position? _position;
  StreamSubscription<Position>? _positions;
  bool _started = false; // first load picks the stop to resume at
  int? _arrivedAt; // the stop whose block the terminal saw the truck reach
  final _turnsSaid = <String>{}; // "stop|turn index|ahead" or "…|now": each prompt once

  String t(String key, [Map<String, String> values = const {}]) => tr(widget.lang, key, values);

  @override
  void initState() {
    super.initState();
    widget.api.voiceOn().then((on) {
      if (mounted) setState(() => _voiceOn = on);
    });
    _follow();
    // Inside the terminal the position keeps going to the terminal (also locked): it sees the truck reach the block.
    widget.api.tracker.follow(widget.appointmentNo, widget.lang, inside: true);
    widget.api.tracker.addListener(_onTracking);
    _load();
    // Re-read every 30 s: a container moved in the yard shows up here.
    _timer = Timer.periodic(const Duration(seconds: 30), (_) => _load());
    // Re-check "driving" every few seconds, so the button comes back soon after the truck stops.
    _tick = Timer.periodic(const Duration(seconds: 5), (_) {
      if (mounted && _position != null) setState(() {});
    });
  }

  @override
  void dispose() {
    _timer?.cancel();
    _tick?.cancel();
    _positions?.cancel();
    widget.api.tracker.removeListener(_onTracking);
    _voice.stop();
    super.dispose();
  }

  Future<void> _load() async {
    try {
      Position? position = _position;
      if (position == null) {
        try {
          final permission = await locations.checkPermission();
          if (permission == LocationPermission.always || permission == LocationPermission.whileInUse) {
            position = await locations.current(const LocationSettings(accuracy: LocationAccuracy.high))
                .timeout(const Duration(seconds: 8));
          }
        } catch (_) {
          // No position: the route starts at the gate.
        }
      }
      final guidance = await widget.api.yard(widget.appointmentNo, latitude: position?.latitude, longitude: position?.longitude);
      if (!mounted) return;
      setState(() {
        _guidance = guidance;
        _error = null;
        if (position != null) {
          _truck = [position.longitude, position.latitude];
          _position ??= position;
        }
        if (!_started) {
          _current = guidance.firstOpen;
          _started = true;
        }
        if (_current >= guidance.stops.length) _current = guidance.stops.length - 1;
        if (guidance.stops[_current].arrived) _arrivedAt = guidance.stops[_current].sequence;
      });
      _speak();
    } on ApiException catch (e) {
      if (mounted) setState(() => _error = e.message);
    } catch (_) {
      if (mounted) setState(() => _error = t('offline'));
    }
  }

  /// The terminal saw the truck reach the current stop's block: say so once.
  void _onTracking() {
    final at = widget.api.tracker.last?.atStop;
    final g = _guidance;
    if (!mounted || at == null || g == null || at == _arrivedAt) return;
    if (g.stops[_current].sequence != at) return;
    setState(() => _arrivedAt = at);
    if (_voiceOn) _voice.sayText(widget.lang, 'arrivedHere');
  }

  /// "Done here": the next stop becomes current; the terminal is told (if offline, the screen still moves on).
  void _done(YardGuidance g) {
    final stop = g.stops[_current];
    setState(() => _current++);
    widget.api.completeStop(widget.appointmentNo, stop.sequence).catchError((_) {});
    _speak();
  }

  /// Driving: faster than about 9 km/h on a fix from the last 15 s.
  bool get _moving {
    final p = _position;
    return p != null && p.speed > 2.5 && DateTime.now().difference(p.timestamp) < const Duration(seconds: 15);
  }

  /// The truck's dot on the map and whether it is moving, live while this screen is open.
  Future<void> _follow() async {
    try {
      final permission = await locations.checkPermission();
      if (permission != LocationPermission.always && permission != LocationPermission.whileInUse) return;
      _positions = locations.stream(const LocationSettings(accuracy: LocationAccuracy.high))
          .listen((p) {
        if (!mounted) return;
        setState(() {
          _position = p;
          _truck = [p.longitude, p.latitude];
        });
        _promptTurn();
      }, onError: (Object _) {});
    } catch (_) {
      // No location: the route starts at the gate and the button stays available.
    }
  }

  /// The next turn on the way to the current stop and how far it is, while there is a position.
  (YardTurn, double)? get _nextTurn {
    final g = _guidance;
    final truck = _truck;
    if (g == null || truck == null || g.stops.isEmpty || _arrivedAt == g.stops[_current].sequence) return null;
    return g.legTo(g.stops[_current].sequence)?.nextTurn(truck);
  }

  /// Junction prompts: once ahead (25–80 m) and once at the turn (under 20 m).
  void _promptTurn() {
    final next = _nextTurn;
    final g = _guidance;
    if (next == null || g == null || !_voiceOn) return;
    final (turn, meters) = next;
    final leg = g.legTo(g.stops[_current].sequence)!;
    final key = '${leg.toStop}|${leg.turns.indexOf(turn)}';
    if (meters < 20 && _turnsSaid.add('$key|now')) {
      _turnsSaid.add('$key|ahead');
      _voice.sayTurn(widget.lang, turn);
    } else if (meters >= 25 && meters <= 80 && _turnsSaid.add('$key|ahead')) {
      _voice.sayTurn(widget.lang, turn, meters: meters);
    }
  }

  /// Speaks the current stop when it is new or changed (next stop, container moved); [again] repeats it.
  void _speak({bool again = false}) {
    final g = _guidance;
    if (g == null || g.stops.isEmpty || !_voiceOn) return;
    final stop = g.stops[_current];
    final key = '${stop.sequence}|${stop.block}|${stop.slot}|${stop.moved}';
    if (!again && key == _spokenKey) return;
    _spokenKey = key;
    _voice.sayStop(widget.lang, stop, meters: g.legTo(stop.sequence)?.meters);
  }

  void _toggleVoice() {
    setState(() => _voiceOn = !_voiceOn);
    widget.api.saveVoiceOn(_voiceOn);
    if (_voiceOn) {
      _speak(again: true);
    } else {
      _voice.stop();
    }
  }

  String _clock(DateTime? at) {
    if (at == null) return '';
    final l = at.toLocal();
    return '${l.hour.toString().padLeft(2, '0')}:${l.minute.toString().padLeft(2, '0')}';
  }

  /// The full in-gate ticket: appointment, truck, lane and time, and every stop with its position.
  void _showTicket(YardGuidance g) {
    final ticket = g.ticket!;
    showModalBottomSheet<void>(
      context: context,
      showDragHandle: true,
      builder: (context) => SafeArea(
        child: ListView(
          shrinkWrap: true,
          padding: const EdgeInsets.fromLTRB(20, 0, 20, 20),
          children: [
            Text(t('gateTicket'), style: const TextStyle(fontSize: 20, fontWeight: FontWeight.w600, color: ink)),
            const SizedBox(height: 4),
            Text(ticket.terminalName, style: const TextStyle(fontSize: 15, color: muted)),
            const SizedBox(height: 12),
            _TicketRow(label: t('appointment'), value: g.appointmentNo, ltr: true),
            if (ticket.truckPlate != null) _TicketRow(label: t('truck'), value: ticket.truckPlate!, ltr: true),
            if (ticket.lane != null) _TicketRow(label: t('lane'), value: ticket.lane!, ltr: true),
            if (ticket.gateInAt != null) _TicketRow(label: t('gateInTime'), value: _clock(ticket.gateInAt), ltr: true),
            const SizedBox(height: 12),
            Text(t('yourStops'), style: const TextStyle(fontSize: 15, color: muted)),
            for (final s in g.stops)
              Padding(
                padding: const EdgeInsets.symmetric(vertical: 6),
                child: Row(crossAxisAlignment: CrossAxisAlignment.start, children: [
                  SizedBox(width: 28, child: Text('${s.sequence}', style: const TextStyle(fontSize: 17, fontWeight: FontWeight.w600, color: ink))),
                  Expanded(
                    child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                      Text(_title(s), style: const TextStyle(fontSize: 17, color: ink)),
                      if (_slotText(s) != null) Text(_slotText(s)!, style: const TextStyle(fontSize: 15, color: muted)),
                      if (s.containers.isNotEmpty)
                        Text(s.containers.join('  '), textDirection: TextDirection.ltr, style: const TextStyle(fontSize: 15, color: muted, letterSpacing: 0.5)),
                    ]),
                  ),
                ]),
              ),
          ],
        ),
      ),
    );
  }

  String _title(YardStop stop) => switch (stop.kind) {
        'EXIT' => t('exitStop'),
        _ when stop.block == null => t('noPosition'),
        'DROP' => t('dropAt', {'block': stop.block!}),
        _ => t('pickupAt', {'block': stop.block!}),
      };

  String? _slotText(YardStop stop) {
    final p = stop.slotParts;
    if (p.isEmpty) return null;
    if (p.length >= 3) return t('rowBayTier', {'row': p[0], 'bay': p[1], 'tier': p[2]});
    return stop.slot;
  }

  @override
  Widget build(BuildContext context) {
    final g = _guidance;
    return Scaffold(
      appBar: AppBar(title: Text(t('whereToGo')), actions: [
        if (_voiceOn && g != null) IconButton(tooltip: t('sayAgain'), icon: const Icon(Icons.replay), onPressed: () => _speak(again: true)),
        IconButton(
          tooltip: _voiceOn ? t('voiceOn') : t('voiceOff'),
          icon: Icon(_voiceOn ? Icons.volume_up : Icons.volume_off),
          onPressed: _toggleVoice,
        ),
      ]),
      body: g == null
          ? Center(child: _error == null ? const CircularProgressIndicator() : Padding(padding: const EdgeInsets.all(20), child: ErrorBox(message: _error!)))
          : Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                _NextStop(
                  stop: g.stops[_current],
                  title: _title(g.stops[_current]),
                  slot: _slotText(g.stops[_current]),
                  progress: t('stopOf', {'n': '${_current + 1}', 'total': '${g.stops.length}'}),
                  meters: g.legTo(g.stops[_current].sequence)?.meters,
                  arrived: _arrivedAt == g.stops[_current].sequence,
                  lang: widget.lang,
                  t: t,
                ),
                if (_nextTurn case (final turn, final meters)) _TurnStrip(turn: turn, meters: meters, lang: widget.lang),
                if (g.ticket != null) _TicketStrip(ticket: g.ticket!, t: t, onOpen: () => _showTicket(g)),
                if (!g.live) Padding(padding: const EdgeInsets.symmetric(horizontal: 16), child: Text(t('yardOffline'), style: const TextStyle(color: red))),
                Expanded(
                  child: Container(
                    margin: const EdgeInsets.all(12),
                    decoration: BoxDecoration(color: Colors.white, borderRadius: BorderRadius.circular(6), border: Border.all(color: line)),
                    child: ClipRRect(
                      borderRadius: BorderRadius.circular(6),
                      child: CustomPaint(
                        painter: YardPainter(guidance: g, currentStop: g.stops[_current].sequence, truck: _truck),
                        child: const SizedBox.expand(),
                      ),
                    ),
                  ),
                ),
                if (_current < g.stops.length - 1)
                  Padding(
                    padding: const EdgeInsets.fromLTRB(12, 0, 12, 16),
                    // Not while driving: the next stop is spoken, nothing needs a tap until the truck stands.
                    child: FilledButton(
                      onPressed: _moving ? null : () => _done(g),
                      child: Text(_moving ? t('stopToConfirm') : t('doneHere')),
                    ),
                  ),
              ],
            ),
    );
  }
}

/// The next turn: arrow, what to do and where, how far.
class _TurnStrip extends StatelessWidget {
  const _TurnStrip({required this.turn, required this.meters, required this.lang});

  final YardTurn turn;
  final double meters;
  final String lang;

  static const _icons = {
    'SlightLeft': Icons.turn_slight_left,
    'Left': Icons.turn_left,
    'SharpLeft': Icons.turn_sharp_left,
    'SlightRight': Icons.turn_slight_right,
    'Right': Icons.turn_right,
    'SharpRight': Icons.turn_sharp_right,
    'UTurn': Icons.u_turn_left,
  };

  @override
  Widget build(BuildContext context) {
    return Container(
      color: const Color(0xFF1A3D51),
      padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 10),
      child: Row(children: [
        // Left is left whatever the language: the arrow is never mirrored.
        Directionality(textDirection: TextDirection.ltr, child: Icon(_icons[turn.turn] ?? Icons.straight, color: Colors.white, size: 34)),
        const SizedBox(width: 12),
        Expanded(
          child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
            Text(tr(lang, 'nextTurn'), style: const TextStyle(color: Color(0xFFB9C7D0), fontSize: 13)),
            Text(turnWhat(lang, turn), style: const TextStyle(color: Colors.white, fontSize: 18, fontWeight: FontWeight.w600)),
          ]),
        ),
        Text(tr(lang, 'meters', {'m': '${meters.round()}'}), style: const TextStyle(color: Colors.white, fontSize: 18, fontWeight: FontWeight.w600)),
      ]),
    );
  }
}

class _TicketStrip extends StatelessWidget {
  const _TicketStrip({required this.ticket, required this.t, required this.onOpen});

  final GateTicket ticket;
  final String Function(String, [Map<String, String>]) t;
  final VoidCallback onOpen;

  @override
  Widget build(BuildContext context) {
    return Material(
      color: const Color(0xFFE3F1F2),
      child: InkWell(
        onTap: onOpen,
        child: Padding(
          padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 10),
          child: Row(children: [
            const Icon(Icons.confirmation_number_outlined, color: ink, size: 20),
            const SizedBox(width: 10),
            Expanded(child: Text(t('gateTicket'), style: const TextStyle(fontSize: 15, color: ink, fontWeight: FontWeight.w600))),
            if (ticket.lane != null) Text(ticket.lane!, textDirection: TextDirection.ltr, style: const TextStyle(fontSize: 15, color: ink)),
            const SizedBox(width: 6),
            const Icon(Icons.expand_more, color: ink),
          ]),
        ),
      ),
    );
  }
}

class _TicketRow extends StatelessWidget {
  const _TicketRow({required this.label, required this.value, this.ltr = false});

  final String label;
  final String value;
  final bool ltr;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 3),
      child: Row(children: [
        Expanded(child: Text(label, style: const TextStyle(fontSize: 15, color: muted))),
        Text(value, textDirection: ltr ? TextDirection.ltr : null, style: const TextStyle(fontSize: 17, color: ink, fontWeight: FontWeight.w600)),
      ]),
    );
  }
}

class _NextStop extends StatelessWidget {
  const _NextStop({required this.stop, required this.title, required this.slot, required this.progress, required this.meters, required this.lang, required this.t, this.arrived = false});

  final bool arrived;

  final YardStop stop;
  final String title;
  final String? slot;
  final String progress;
  final double? meters;
  final String lang;
  final String Function(String, [Map<String, String>]) t;

  @override
  Widget build(BuildContext context) {
    return Container(
      color: ink,
      padding: const EdgeInsets.fromLTRB(16, 12, 16, 16),
      child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
        Row(children: [
          Text(progress, style: const TextStyle(color: Color(0xFFB9C7D0), fontSize: 14)),
          const Spacer(),
          if (meters != null) Text(t('meters', {'m': '${meters!.round()}'}), style: const TextStyle(color: Color(0xFFB9C7D0), fontSize: 14)),
        ]),
        const SizedBox(height: 4),
        Text(title, style: const TextStyle(color: Colors.white, fontSize: 26, fontWeight: FontWeight.w600)),
        if (slot != null) Text(slot!, style: const TextStyle(color: Colors.white, fontSize: 18)),
        if (stop.containers.isNotEmpty) ...[
          const SizedBox(height: 6),
          Wrap(spacing: 8, children: [
            for (final c in stop.containers)
              Text(c, textDirection: TextDirection.ltr, style: const TextStyle(color: Colors.white, fontSize: 16, letterSpacing: 0.5)),
          ]),
        ],
        if (arrived) ...[
          const SizedBox(height: 8),
          Row(children: [
            const Icon(Icons.check_circle, color: Color(0xFF7FD69B), size: 20),
            const SizedBox(width: 8),
            Expanded(child: Text(t('arrivedHere'), style: const TextStyle(color: Color(0xFF7FD69B), fontSize: 15, fontWeight: FontWeight.w600))),
          ]),
        ],
        if (stop.moved) ...[
          const SizedBox(height: 6),
          Text(t('moved'), style: const TextStyle(color: amber, fontSize: 15, fontWeight: FontWeight.w600)),
        ],
      ]),
    );
  }
}

/// Blocks, roads, the route to the current stop (strong) and the rest (faint), stops and the truck.
class YardPainter extends CustomPainter {
  YardPainter({required this.guidance, required this.currentStop, this.truck});

  final YardGuidance guidance;
  final int currentStop;
  final List<double>? truck;

  @override
  void paint(Canvas canvas, Size size) {
    final g = guidance;
    if (g.bounds[2] <= g.bounds[0] || g.bounds[3] <= g.bounds[1]) return;
    final proj = YardProjection(g.bounds, size.width, size.height);
    Offset at(List<double> p) {
      final (x, y) = proj.toScreen(p);
      return Offset(x, y);
    }

    Path pathOf(List<List<double>> points) {
      final path = Path()..moveTo(at(points.first).dx, at(points.first).dy);
      for (final p in points.skip(1)) {
        path.lineTo(at(p).dx, at(p).dy);
      }
      return path;
    }

    final stop = g.stops.where((s) => s.sequence == currentStop).firstOrNull;

    // Areas: blocks grey, the target block teal, gates and exits outlined.
    for (final area in g.areas) {
      if (area.ring.length < 3) continue;
      final path = pathOf(area.ring)..close();
      final isTarget = stop?.block != null && area.block == stop!.block;
      final fill = Paint()
        ..color = isTarget ? teal : (area.kind == 'YARD_ZONE' || area.kind == 'CFS' ? const Color(0xFFDDE4E9) : Colors.white);
      canvas.drawPath(path, fill);
      canvas.drawPath(path, Paint()
        ..style = PaintingStyle.stroke
        ..strokeWidth = isTarget ? 2 : 1
        ..color = isTarget ? teal : line);
      final label = area.block ?? (area.kind == 'GATE' || area.kind == 'EXIT' ? area.name : null);
      if (label != null) {
        final c = _centre(area.ring.map(at).toList());
        final tp = TextPainter(
          text: TextSpan(text: label, style: TextStyle(color: isTarget ? Colors.white : ink, fontSize: isTarget ? 16 : 12, fontWeight: FontWeight.w600)),
          textDirection: TextDirection.ltr,
        )..layout();
        tp.paint(canvas, c - Offset(tp.width / 2, tp.height / 2));
      }
    }

    // Roads.
    final road = Paint()
      ..style = PaintingStyle.stroke
      ..strokeWidth = 7
      ..strokeCap = StrokeCap.round
      ..strokeJoin = StrokeJoin.round
      ..color = const Color(0xFFC9D3DA);
    for (final r in g.roads) {
      if (r.length >= 2) canvas.drawPath(pathOf(r), road);
    }

    // Routes: later legs faint, the current one strong.
    for (final leg in g.legs) {
      if (leg.path.length < 2) continue;
      final current = leg.toStop == currentStop;
      canvas.drawPath(pathOf(leg.path), Paint()
        ..style = PaintingStyle.stroke
        ..strokeWidth = current ? 5 : 3
        ..strokeCap = StrokeCap.round
        ..strokeJoin = StrokeJoin.round
        ..color = current ? teal : teal.withValues(alpha: 0.25));
    }

    // Stop markers, numbered.
    for (final s in g.stops) {
      final p = s.point;
      if (p == null) continue;
      final o = at(p);
      final isCurrent = s.sequence == currentStop;
      canvas.drawCircle(o, isCurrent ? 13 : 10, Paint()..color = Colors.white);
      canvas.drawCircle(o, isCurrent ? 11 : 8, Paint()..color = isCurrent ? ink : muted);
      final tp = TextPainter(
        text: TextSpan(text: '${s.sequence}', style: const TextStyle(color: Colors.white, fontSize: 11, fontWeight: FontWeight.w700)),
        textDirection: TextDirection.ltr,
      )..layout();
      tp.paint(canvas, o - Offset(tp.width / 2, tp.height / 2));
    }

    // The truck.
    if (truck != null) {
      final o = at(truck!);
      canvas.drawCircle(o, 9, Paint()..color = Colors.white);
      canvas.drawCircle(o, 7, Paint()..color = const Color(0xFF2F5D78));
    }
  }

  Offset _centre(List<Offset> points) {
    var x = 0.0;
    var y = 0.0;
    final n = points.length > 1 && points.first == points.last ? points.length - 1 : points.length;
    for (var i = 0; i < n; i++) {
      x += points[i].dx;
      y += points[i].dy;
    }
    return Offset(x / n, y / n);
  }

  @override
  bool shouldRepaint(covariant YardPainter old) => old.guidance != guidance || old.currentStop != currentStop || old.truck != truck;
}

