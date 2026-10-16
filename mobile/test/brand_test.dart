import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:yardsyncs_mobile/brand.dart';

void main() {
  test('the server\'s brand is read, with Yardsyncs for anything missing', () {
    final brand = AppBrand.fromJson({'name': 'Harbor One', 'color': '#7a1f3d', 'logoUrl': '/api/branding/2/logo?v=1'}, 'https://tas.harbor-one.example');
    expect(brand.name, 'Harbor One');
    expect(brand.color, const Color(0xFF7A1F3D));
    expect(brand.logoUrl, 'https://tas.harbor-one.example/api/branding/2/logo?v=1');
    expect(brand.isYardsyncs, isFalse);

    final plain = AppBrand.fromJson({'name': '', 'color': 'teal', 'logoUrl': null}, 'https://x');
    expect(plain.name, 'Yardsyncs');
    expect(plain.color, AppBrand.yardsyncs.color);
    expect(plain.logoUrl, isNull);
    expect(plain.isYardsyncs, isTrue);
  });

  test('colours are read from #rrggbb only', () {
    expect(parseColor('#0E7C86'), const Color(0xFF0E7C86));
    expect(parseColor('0e7c86'), isNull);
    expect(parseColor(null), isNull);
  });
}
