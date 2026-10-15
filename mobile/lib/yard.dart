// Directions inside the terminal (GET /api/driver/appointments/{no}/yard).

import 'dart:math' as math;

class YardStop {
  const YardStop({required this.sequence, required this.kind, this.block, this.slot, required this.containers, required this.categoriesEn, required this.categoriesAr, required this.moved, this.point});

  final int sequence;
  final String kind; // DROP, PICKUP, EXIT
  final String? block;
  final String? slot; // row.bay.tier
  final List<String> containers;
  final List<String> categoriesEn;
  final List<String> categoriesAr;
  final bool moved;
  final List<double>? point; // [lon, lat]

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
      );

  /// Row, bay and tier from "14.03.2" (any part may be missing).
  List<String> get slotParts => slot == null ? const [] : slot!.split('.');
}

class YardLeg {
  const YardLeg({required this.toStop, required this.path, required this.meters});

  final int toStop;
  final List<List<double>> path;
  final double meters;

  factory YardLeg.fromJson(Map<String, dynamic> json) => YardLeg(
        toStop: (json['toStop'] as num).toInt(),
        path: _points(json['path']),
        meters: (json['meters'] as num).toDouble(),
      );
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

class YardGuidance {
  const YardGuidance({required this.stops, required this.legs, required this.areas, required this.roads, required this.bounds, required this.live});

  final List<YardStop> stops;
  final List<YardLeg> legs;
  final List<YardArea> areas;
  final List<List<List<double>>> roads;
  final List<double> bounds; // minLon, minLat, maxLon, maxLat
  final bool live;

  factory YardGuidance.fromJson(Map<String, dynamic> json) {
    final map = json['map'] as Map<String, dynamic>;
    return YardGuidance(
      stops: ((json['stops'] as List?) ?? const []).map((e) => YardStop.fromJson(e as Map<String, dynamic>)).toList(),
      legs: ((json['legs'] as List?) ?? const []).map((e) => YardLeg.fromJson(e as Map<String, dynamic>)).toList(),
      areas: ((map['areas'] as List?) ?? const []).map((e) => YardArea.fromJson(e as Map<String, dynamic>)).toList(),
      roads: ((map['roads'] as List?) ?? const []).map(_points).toList(),
      bounds: ((map['bounds'] as List?) ?? const [0, 0, 0, 0]).map((e) => (e as num).toDouble()).toList(),
      live: json['live'] as bool? ?? true,
    );
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
