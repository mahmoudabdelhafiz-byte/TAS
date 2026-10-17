// Directions inside the terminal (GET /api/driver/appointments/{no}/yard).

import 'dart:math' as math;

class YardStop {
  const YardStop({
    required this.sequence,
    required this.kind,
    this.block,
    this.slot,
    required this.containers,
    required this.categoriesEn,
    required this.categoriesAr,
    required this.moved,
    this.point,
    this.arrived = false,
    this.done = false,
  });

  final int sequence;
  final String kind; // DROP, PICKUP, EXIT
  final String? block;
  final String? slot; // row.bay.tier
  final List<String> containers;
  final List<String> categoriesEn;
  final List<String> categoriesAr;
  final bool moved;
  final List<double>? point; // [lon, lat]

  /// The truck reached the block (the terminal was told).
  final bool arrived;

  /// The driver confirmed the stop.
  final bool done;

  factory YardStop.fromJson(Map<String, dynamic> json) => YardStop(
        sequence: (json['sequence'] as num).toInt(),
        kind: json['kind'] as String,
        block: json['block'] as String?,
        slot: json['slot'] as String?,
        containers: ((json['containers'] as List?) ?? const []).cast<String>(),
        categoriesEn: ((json['categoriesEn'] as List?) ?? const []).cast<String>(),
        categoriesAr: ((json['categoriesAr'] as List?) ?? const []).cast<String>(),
        moved: json['moved'] as bool? ?? false,
        point: (json['point'] as List?)?.map((e) => (e as num).toDouble()).toList(),
        arrived: json['arrived'] as bool? ?? false,
        done: json['done'] as bool? ?? false,
      );

  /// Row, bay and tier from "14.03.2" (any part may be missing).
  List<String> get slotParts => slot == null ? const [] : slot!.split('.');
}

class YardLeg {
  const YardLeg({required this.toStop, required this.path, required this.meters, this.turns = const []});

  final int toStop;
  final List<List<double>> path;
  final double meters;

  /// Turns at junctions on the way, in order.
  final List<YardTurn> turns;

  factory YardLeg.fromJson(Map<String, dynamic> json) => YardLeg(
        toStop: (json['toStop'] as num).toInt(),
        path: _points(json['path']),
        meters: (json['meters'] as num).toDouble(),
        turns: ((json['turns'] as List?) ?? const []).map((e) => YardTurn.fromJson(e as Map<String, dynamic>)).toList(),
      );

  /// How far along the drive the truck is: the distance to the route point nearest to it.
  double progress(List<double> truck) {
    if (path.isEmpty) return 0;
    var best = 0;
    var bestMeters = double.infinity;
    for (var i = 0; i < path.length; i++) {
      final m = metersBetween(path[i], truck);
      if (m < bestMeters) {
        bestMeters = m;
        best = i;
      }
    }
    var along = 0.0;
    for (var i = 1; i <= best; i++) {
      along += metersBetween(path[i - 1], path[i]);
    }
    return along;
  }

  /// The next turn ahead of the truck and how far it is (straight line), or null when no turn is left.
  (YardTurn, double)? nextTurn(List<double> truck) {
    final at = progress(truck);
    for (final turn in turns) {
      // A few metres of slack: the nearest route point may already be the junction.
      if (turn.atMeters + 8 >= at) return (turn, metersBetween(turn.point, truck));
    }
    return null;
  }
}

/// A turn at a junction: SlightLeft, Left, SharpLeft, SlightRight, Right, SharpRight or UTurn.
class YardTurn {
  const YardTurn({required this.point, required this.turn, required this.atMeters, this.near, this.nearIsBlock = false});

  final List<double> point; // [lon, lat]
  final String turn;
  final double atMeters;

  /// The block (or named area) beside the junction.
  final String? near;
  final bool nearIsBlock;

  factory YardTurn.fromJson(Map<String, dynamic> json) => YardTurn(
        point: ((json['point'] as List?) ?? const [0, 0]).map((e) => (e as num).toDouble()).toList(),
        turn: (json['turn'] as String?) ?? 'Right',
        atMeters: (json['atMeters'] as num?)?.toDouble() ?? 0,
        near: json['near'] as String?,
        nearIsBlock: json['nearIsBlock'] as bool? ?? false,
      );
}

/// Great-circle distance in metres between two [lon, lat] points.
double metersBetween(List<double> a, List<double> b) {
  const r = 6371000.0;
  final dLat = (b[1] - a[1]) * math.pi / 180;
  final dLon = (b[0] - a[0]) * math.pi / 180;
  final h = math.sin(dLat / 2) * math.sin(dLat / 2) +
      math.cos(a[1] * math.pi / 180) * math.cos(b[1] * math.pi / 180) * math.sin(dLon / 2) * math.sin(dLon / 2);
  return 2 * r * math.asin(math.min(1, math.sqrt(h)));
}

class YardArea {
  const YardArea({required this.name, required this.kind, this.block, required this.ring});

  final String name;
  final String kind;
  final String? block;
  final List<List<double>> ring;

  factory YardArea.fromJson(Map<String, dynamic> json) => YardArea(
        name: json['name'] as String,
        kind: json['kind'] as String,
        block: json['block'] as String?,
        ring: _points(json['ring']),
      );
}

/// The in-gate ticket: when and where the truck passed the gate.
class GateTicket {
  const GateTicket({required this.terminalName, this.gateInAt, this.lane, this.truckPlate});

  final String terminalName;
  final DateTime? gateInAt;
  final String? lane;
  final String? truckPlate;

  factory GateTicket.fromJson(Map<String, dynamic> json) => GateTicket(
        terminalName: (json['terminalName'] as String?) ?? '',
        gateInAt: json['gateInAt'] == null ? null : DateTime.parse(json['gateInAt'] as String).toUtc(),
        lane: json['lane'] as String?,
        truckPlate: json['truckPlate'] as String?,
      );
}

class YardGuidance {
  const YardGuidance({
    required this.appointmentNo,
    required this.stops,
    required this.legs,
    required this.areas,
    required this.roads,
    required this.bounds,
    required this.live,
    this.ticket,
  });

  final String appointmentNo;
  final GateTicket? ticket;

  final List<YardStop> stops;
  final List<YardLeg> legs;
  final List<YardArea> areas;
  final List<List<List<double>>> roads;
  final List<double> bounds; // minLon, minLat, maxLon, maxLat
  final bool live;

  factory YardGuidance.fromJson(Map<String, dynamic> json) {
    final map = json['map'] as Map<String, dynamic>;
    return YardGuidance(
      appointmentNo: (json['appointmentNo'] as String?) ?? '',
      ticket: json['ticket'] == null ? null : GateTicket.fromJson(json['ticket'] as Map<String, dynamic>),
      stops: ((json['stops'] as List?) ?? const []).map((e) => YardStop.fromJson(e as Map<String, dynamic>)).toList(),
      legs: ((json['legs'] as List?) ?? const []).map((e) => YardLeg.fromJson(e as Map<String, dynamic>)).toList(),
      areas: ((map['areas'] as List?) ?? const []).map((e) => YardArea.fromJson(e as Map<String, dynamic>)).toList(),
      roads: ((map['roads'] as List?) ?? const []).map(_points).toList(),
      bounds: ((map['bounds'] as List?) ?? const [0, 0, 0, 0]).map((e) => (e as num).toDouble()).toList(),
      live: json['live'] as bool? ?? true,
    );
  }

  /// Where to resume: the first stop not confirmed yet (the exit when all are done).
  int get firstOpen {
    final i = stops.indexWhere((s) => !s.done);
    return i < 0 ? stops.length - 1 : i;
  }

  YardLeg? legTo(int stop) {
    for (final leg in legs) {
      if (leg.toStop == stop) return leg;
    }
    return null;
  }
}

List<List<double>> _points(dynamic list) =>
    ((list as List?) ?? const []).map((p) => (p as List).map((e) => (e as num).toDouble()).toList()).toList();

/// Map projection for a small area: longitude scaled by cos(latitude) so blocks keep their shape.
class YardProjection {
  YardProjection(List<double> bounds, double width, double height, {double padding = 16}) {
    final midLat = (bounds[1] + bounds[3]) / 2;
    _kx = _cos(midLat);
    _minX = bounds[0] * _kx;
    _minY = bounds[1];
    final spanX = (bounds[2] - bounds[0]) * _kx;
    final spanY = bounds[3] - bounds[1];
    final usableW = width - 2 * padding;
    final usableH = height - 2 * padding;
    _scale = spanX <= 0 || spanY <= 0 ? 1 : (usableW / spanX < usableH / spanY ? usableW / spanX : usableH / spanY);
    _offsetX = padding + (usableW - spanX * _scale) / 2;
    _offsetY = padding + (usableH - spanY * _scale) / 2;
    _height = height;
  }

  late final double _kx;
  late final double _minX;
  late final double _minY;
  late final double _scale;
  late final double _offsetX;
  late final double _offsetY;
  late final double _height;

  /// Screen x / y (north up) for [lon, lat].
  (double, double) toScreen(List<double> p) =>
      (_offsetX + (p[0] * _kx - _minX) * _scale, _height - (_offsetY + (p[1] - _minY) * _scale));

  static double _cos(double degrees) => math.cos(degrees * math.pi / 180);
}
