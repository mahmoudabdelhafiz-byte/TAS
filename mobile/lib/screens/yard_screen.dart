import 'dart:async';

import 'package:flutter/material.dart';
import 'package:geolocator/geolocator.dart';

import '../api.dart';
import '../strings.dart';
import '../theme.dart';
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

  String t(String key, [Map<String, String> values = const {}]) => tr(widget.lang, key, values);

  @override
  void initState() {
    super.initState();
    _load();
    // Re-read every 30 s: a container moved in the yard shows up here.
    _timer = Timer.periodic(const Duration(seconds: 30), (_) => _load());
  }

  @override
  void dispose() {
    _timer?.cancel();
    super.dispose();
  }

  Future<void> _load() async {
    try {
      Position? position;
      try {
        final permission = await Geolocator.checkPermission();
        if (permission == LocationPermission.always || permission == LocationPermission.whileInUse) {
          position = await Geolocator.getCurrentPosition(locationSettings: const LocationSettings(accuracy: LocationAccuracy.high))
              .timeout(const Duration(seconds: 8));
        }
      } catch (_) {
        // No position: the route starts at the gate.
      }
      final guidance = await widget.api.yard(widget.appointmentNo, latitude: position?.latitude, longitude: position?.longitude);
      if (!mounted) return;
      setState(() {
        _guidance = guidance;
        _error = null;
        if (position != null) _truck = [position.longitude, position.latitude];
        if (_current >= guidance.stops.length) _current = guidance.stops.length - 1;
      });
    } on ApiException catch (e) {
      if (mounted) setState(() => _error = e.message);
    } catch (_) {
      if (mounted) setState(() => _error = t('offline'));
    }
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
      appBar: AppBar(title: Text(t('whereToGo'))),
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
                  lang: widget.lang,
                  t: t,
                ),
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
                    child: FilledButton(onPressed: () => setState(() => _current++), child: Text(t('doneHere'))),
                  ),
              ],
            ),
    );
  }
}

class _NextStop extends StatelessWidget {
  const _NextStop({required this.stop, required this.title, required this.slot, required this.progress, required this.meters, required this.lang, required this.t});

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

