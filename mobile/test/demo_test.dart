import 'package:flutter_test/flutter_test.dart';
import 'package:geolocator/geolocator.dart';
import 'package:yardsyncs_mobile/api.dart';
import 'package:yardsyncs_mobile/demo.dart';
import 'package:yardsyncs_mobile/location.dart';
import 'package:yardsyncs_mobile/models.dart';

class MemoryStore implements SessionStore {
  final values = <String, String>{};

  @override
  Future<String?> read(String key) async => values[key];

  @override
  Future<void> write(String key, String? value) async {
    if (value == null) {
      values.remove(key);
    } else {
      values[key] = value;
    }
  }
}

void main() {
  var now = DateTime.utc(2026, 10, 18, 7, 20);
  late DemoWorld world;
  late DriverApi api;

  setUp(() {
    now = DateTime.utc(2026, 10, 18, 7, 20);
    world = DemoWorld(clock: () => now);
    api = DriverApi(store: MemoryStore(), client: startDemo(world));
  });

  /// Drives for [seconds] and sends the position, as the app does.
  Future<TripUpdate> drive([int seconds = 5]) async {
    now = now.add(Duration(seconds: seconds));
    final p = await locations.current(const LocationSettings());
    return api.reportPosition(DemoWorld.todayNo, p.latitude, p.longitude, p.speed);
  }

  test('any number and any 6 digits sign in; other codes are refused', () async {
    await api.requestCode('0500000001');
    await expectLater(api.verify('0500000001', '12'), throwsA(isA<ApiException>()));
    final me = await api.verify('0500000001', '123456');
    expect(me.fullName, isNotEmpty);
    expect(await locations.checkPermission(), LocationPermission.always);
  });

  test('today\'s visit: on the way, through the gate, each stop, out', () async {
    await api.verify('0500000001', '123456');
    final list = await api.appointments();
    expect(list.map((a) => a.status), ['Confirmed', 'Confirmed', 'Completed']);
    expect(list.first.canStartTrip, isTrue);
    expect(list[1].trafficLight, 'Amber');
    expect((await api.gatePass())!.payload(DemoWorld.todayNo, now), startsWith('YS1:1:${DemoWorld.todayNo}:'));

    expect((await api.startTrip(DemoWorld.todayNo)).status, 'EnRoute');
    await expectLater(api.yard(DemoWorld.todayNo), throwsA(isA<ApiException>())); // not inside yet

    var update = await drive();
    expect(update.status, 'EnRoute');
    expect(update.eta, isNotNull);
    expect(update.distanceKm, greaterThan(0));
    for (var i = 0; i < 40 && update.status == 'EnRoute'; i++) {
      update = await drive();
    }
    expect(update.status, 'GateIn');
    expect(update.checkedIn, isTrue);

    var guidance = await api.yard(DemoWorld.todayNo);
    expect(guidance.stops.map((s) => s.kind), ['DROP', 'PICKUP', 'EXIT']);
    expect(guidance.stops.first.block, 'E1');
    expect(guidance.legTo(1)!.turns.single.turn, 'Right');
    expect(guidance.legTo(2)!.turns, hasLength(2));
    expect(guidance.ticket!.lane, 'IN-1');
    expect(guidance.bounds, hasLength(4));

    for (final stop in [1, 2]) {
      for (var i = 0; i < 60 && update.atStop != stop; i++) {
        update = await drive();
      }
      expect(update.atStop, stop);
      final parked = await locations.current(const LocationSettings());
      expect(parked.speed, 0); // standing still: "Done here" shows
      await api.completeStop(DemoWorld.todayNo, stop);
    }
    guidance = await api.yard(DemoWorld.todayNo);
    expect(guidance.firstOpen, 2);
    for (var i = 0; i < 60 && update.status == 'GateIn'; i++) {
      update = await drive();
    }
    expect(update.status, 'Completed');
    expect((await api.appointments()).first.status, 'Completed');
  });

  test('messages, safety notice, induction and the record', () async {
    await api.verify('0500000001', '123456');
    final quick = await api.quickMessages();
    expect(quick.map((q) => q.code), contains('CALL_ME'));
    final sent = await api.sendMessage(DemoWorld.todayNo, templateCode: 'RUNNING_LATE');
    expect(sent.fromDriver, isTrue);
    expect(await api.messages(DemoWorld.todayNo), hasLength(1));
    final located = await api.sendMessage(DemoWorld.todayNo, latitude: 21.47, longitude: 39.17);
    expect(located.location, isTrue);

    var safety = await api.safety();
    expect(safety.notices.single.confirmed, isFalse);
    expect(safety.inductions.single.needed, isTrue);
    expect(safety.inductions.single.questions, hasLength(2));
    await api.confirmNotice(safety.notices.single.id);
    expect((await api.answerInduction(1, [0, 0])).wrong, [0]);
    final passed = await api.answerInduction(1, [1, 0]);
    expect(passed.passed, isTrue);
    safety = await api.safety();
    expect(safety.notices.single.confirmed, isTrue);
    expect(safety.inductions.single.needed, isFalse);

    var record = await api.myRecord();
    expect(record.entries.single.canDispute, isTrue);
    record = await api.disputeEntry(record.entries.single.id, 'The camera was wrong');
    expect(record.entries.single.disputeStatus, 'Open');
  });

  test('Arabic texts, and signing out starts the demo again', () async {
    api.language = 'ar';
    await api.verify('0500000001', '123456');
    expect((await api.appointments()).first.terminalName, 'محطة الحاويات التجريبية');
    await api.startTrip(DemoWorld.todayNo);
    await api.signOut();
    expect(world.status, 'Confirmed');
  });
}
