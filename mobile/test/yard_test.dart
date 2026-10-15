import 'package:flutter_test/flutter_test.dart';
import 'package:yardsyncs_mobile/yard.dart';

void main() {
  final json = <String, dynamic>{
    'appointmentNo': 'A-1',
    'status': 'GateIn',
    'live': false,
    'ticket': {'terminalName': 'Demo Container Terminal', 'gateInAt': '2026-10-15T07:42:00+00:00', 'lane': 'IN-1', 'truckPlate': '1234 ABJ'},
    'stops': [
      {'sequence': 1, 'kind': 'DROP', 'block': 'E1', 'slot': null, 'containers': ['MSCU5523417'], 'categoriesEn': ['Export drop'], 'categoriesAr': ['تسليم تصدير'], 'moved': false, 'point': [39.1665, 21.464]},
      {'sequence': 2, 'kind': 'PICKUP', 'block': 'B2', 'slot': '14.03.2', 'containers': ['MSCU1234566'], 'categoriesEn': [], 'categoriesAr': [], 'moved': true, 'point': [39.1735, 21.467]},
      {'sequence': 3, 'kind': 'EXIT', 'containers': [], 'moved': false},
    ],
    'legs': [
      {'toStop': 1, 'path': [[39.17, 21.47], [39.17, 21.4655], [39.1665, 21.4655]], 'meters': 650.5},
      {'toStop': 3, 'path': [[39.1735, 21.4655], [39.176, 21.4625]], 'meters': 400},
    ],
    'map': {
      'areas': [
        {'name': 'Block E1', 'kind': 'YARD_ZONE', 'block': 'E1', 'ring': [[39.165, 21.463], [39.168, 21.463], [39.168, 21.465], [39.165, 21.465], [39.165, 21.463]]},
      ],
      'roads': [
        [[39.17, 21.47], [39.17, 21.4625]],
      ],
      'bounds': [39.164, 21.4615, 39.1765, 21.471],
    },
  };

  test('parses stops, legs and the map', () {
    final g = YardGuidance.fromJson(json);
    expect(g.live, isFalse);
    expect(g.appointmentNo, 'A-1');
    expect(g.ticket!.lane, 'IN-1');
    expect(g.ticket!.truckPlate, '1234 ABJ');
    expect(g.ticket!.gateInAt, DateTime.utc(2026, 10, 15, 7, 42));
    expect(YardGuidance.fromJson({...json}..remove('ticket')).ticket, isNull);
    expect(g.stops.map((s) => s.kind), ['DROP', 'PICKUP', 'EXIT']);
    expect(g.stops[1].slotParts, ['14', '03', '2']);
    expect(g.stops[1].moved, isTrue);
    expect(g.stops[0].slotParts, isEmpty);
    expect(g.stops[2].point, isNull);
    expect(g.legTo(1)!.meters, 650.5);
    expect(g.legTo(2), isNull);
    expect(g.legTo(3)!.path.last, [39.176, 21.4625]);
    expect(g.areas.single.block, 'E1');
    expect(g.roads.single.length, 2);
    expect(g.bounds.length, 4);
  });

  test('projection keeps the map inside the screen with north up', () {
    final g = YardGuidance.fromJson(json);
    final p = YardProjection(g.bounds, 400, 600);
    final (x0, y0) = p.toScreen([g.bounds[0], g.bounds[1]]); // south-west
    final (x1, y1) = p.toScreen([g.bounds[2], g.bounds[3]]); // north-east
    for (final v in [x0, x1]) {
      expect(v, inInclusiveRange(16 - 1e-6, 384 + 1e-6));
    }
    for (final v in [y0, y1]) {
      expect(v, inInclusiveRange(16 - 1e-6, 584 + 1e-6));
    }
    expect(x1, greaterThan(x0));
    expect(y1, lessThan(y0)); // north is higher on screen
    // The wider side fills the usable width.
    expect(x1 - x0, closeTo(368, 1e-6));
  });
}
