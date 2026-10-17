import 'package:flutter_test/flutter_test.dart';
import 'package:yardsyncs_mobile/strings.dart';
import 'package:yardsyncs_mobile/trip_tracker.dart';
import 'package:yardsyncs_mobile/voice.dart';
import 'package:yardsyncs_mobile/yard.dart';

YardStop stop(String kind, {String? block, String? slot, bool moved = false}) => YardStop(
    sequence: 1, kind: kind, block: block, slot: slot, containers: const [], categoriesEn: const [], categoriesAr: const [], moved: moved);

void main() {
  test('a pickup is spoken with block, row, bay, tier and distance', () {
    expect(spokenStop('en', stop('PICKUP', block: 'B2', slot: '14.03.2'), meters: 647.3),
        'Pick up at block B 2. Row 14, bay 3, tier 2. About 650 metres');
    expect(spokenStop('en', stop('DROP', block: 'E1'), meters: 12), 'Drop at block E 1');
    expect(spokenStop('en', stop('EXIT')), 'Drive to the exit');
  });

  test('a moved container is said first', () {
    final text = spokenStop('en', stop('PICKUP', block: 'A01', slot: '02.11.1', moved: true));
    expect(text, startsWith(tr('en', 'moved')));
    expect(text, contains('A 01'));
    expect(text, contains('Row 2, bay 11, tier 1'));
  });

  test('every language speaks with its own sentence ends', () {
    expect(spokenStop('ar', stop('PICKUP', block: 'B2', slot: '14.03.2'), meters: 300), contains('حوالي 300 متر'));
    expect(spokenStop('ur', stop('PICKUP', block: 'B2', slot: '14.03.2')), contains('۔ '));
    expect(spokenStop('hi', stop('PICKUP', block: 'B2', slot: '14.03.2')), contains('। '));
    for (final lang in supportedLanguages) {
      expect(speechVoices[lang], isNotEmpty, reason: lang);
      // A voice always reads text written for it.
      for (final (_, textLang) in speechVoices[lang]!) {
        expect(supportedLanguages, contains(textLang));
      }
    }
    expect(speechVoices['ur']!.last, ('hi-IN', 'hi'));
  });

  test('distances are rounded for speech', () {
    expect(spokenMeters(47), 50);
    expect(spokenMeters(94), 90);
    expect(spokenMeters(124), 100);
    expect(spokenMeters(1276), 1300);
  });

  test('positions go out every minute, sooner after a long stretch, never in a burst', () {
    const policy = ReportPolicy();
    final t0 = DateTime(2026, 10, 15, 8);
    expect(policy.due(now: t0), isTrue);
    expect(policy.due(now: t0.add(const Duration(seconds: 30)), lastAt: t0, movedMeters: 200), isFalse);
    expect(policy.due(now: t0.add(const Duration(seconds: 30)), lastAt: t0, movedMeters: 800), isTrue);
    expect(policy.due(now: t0.add(const Duration(seconds: 10)), lastAt: t0, movedMeters: 800), isFalse);
    expect(policy.due(now: t0.add(const Duration(seconds: 61)), lastAt: t0, movedMeters: 0), isTrue);
  });

  test('inside the terminal positions go out more often', () {
    final t0 = DateTime(2026, 10, 15, 8);
    expect(TripTracker.insidePolicy.due(now: t0.add(const Duration(seconds: 16)), lastAt: t0), isTrue);
    expect(TripTracker.insidePolicy.due(now: t0.add(const Duration(seconds: 6)), lastAt: t0, movedMeters: 40), isTrue);
    expect(TripTracker.insidePolicy.due(now: t0.add(const Duration(seconds: 6)), lastAt: t0, movedMeters: 10), isFalse);
  });

  test('turns are said with the distance and the block beside the junction', () {
    const left = YardTurn(point: [0, 0.002], turn: 'Left', atMeters: 222, near: 'A1', nearIsBlock: true);
    expect(turnWhat('en', left), 'Turn left at block A1');
    expect(turnText('en', left, meters: 48), 'In 50 metres, turn left at block A1');
    expect(turnText('en', left, speech: true), 'Now turn left at block A 1');
    expect(turnText('ar', left, meters: 48), 'بعد 50 متر، انعطف يساراً عند البلوك A1');
    expect(turnText('en', const YardTurn(point: [0, 0], turn: 'SlightRight', atMeters: 10, near: 'Truck parking')), 'Now keep right at Truck parking');
    expect(turnText('hi', const YardTurn(point: [0, 0], turn: 'UTurn', atMeters: 10)), 'अभी यू-टर्न लें');
  });
}
