// The brand the app shows: the installation's, or an operator's when the app talks to the operator's own web address
// (GET /api/branding). Yardsyncs until the server answers.

import 'package:flutter/material.dart';

class AppBrand {
  const AppBrand(this.name, this.color, this.logoUrl);

  final String name;
  final Color color;

  /// Absolute address of the logo, or null for the Yardsyncs mark.
  final String? logoUrl;

  static const yardsyncs = AppBrand('Yardsyncs', Color(0xFF0E7C86), null);

  bool get isYardsyncs => name == yardsyncs.name;

  /// From the server's answer; anything missing or malformed keeps the Yardsyncs value.
  static AppBrand fromJson(Map<String, dynamic> json, String base) {
    final name = (json['name'] as String?)?.trim();
    final logo = json['logoUrl'] as String?;
    return AppBrand(
      name == null || name.isEmpty ? yardsyncs.name : name,
      parseColor(json['color'] as String?) ?? yardsyncs.color,
      logo == null || logo.isEmpty ? null : (logo.startsWith('http') ? logo : '$base$logo'),
    );
  }
}

/// "#rrggbb" → a colour, or null.
Color? parseColor(String? hex) {
  if (hex == null || !RegExp(r'^#[0-9a-fA-F]{6}$').hasMatch(hex)) return null;
  return Color(0xFF000000 | int.parse(hex.substring(1), radix: 16));
}

/// The brand in use (set once at start).
AppBrand appBrand = AppBrand.yardsyncs;
