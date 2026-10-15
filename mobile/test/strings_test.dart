import 'package:flutter_test/flutter_test.dart';
import 'package:yardsyncs_mobile/strings.dart';

void main() {
  test('every language has every English text, with the same placeholders', () {
    final keys = texts['en']!.keys.toSet();
    final placeholder = RegExp(r'\{\w+\}');
    for (final lang in supportedLanguages) {
      expect(texts[lang]!.keys.toSet(), keys, reason: lang);
      for (final key in keys) {
        final expected = placeholder.allMatches(texts['en']![key]!).map((m) => m[0]).toSet();
        final actual = placeholder.allMatches(texts[lang]![key]!).map((m) => m[0]).toSet();
        expect(actual, expected, reason: '$lang/$key');
      }
    }
  });

  test('placeholders are filled and unknown keys fall back', () {
    expect(tr('en', 'codeSent', {'mobile': '+966500000001'}), contains('+966500000001'));
    expect(tr('fr', 'signOut'), 'Sign out');
    expect(tr('ar', 'nope'), 'nope');
    expect(isRtl('ar'), isTrue);
    expect(isRtl('ur'), isTrue);
    expect(isRtl('hi'), isFalse);
  });

  test('every appointment status and light has a text', () {
    for (final status in ['Confirmed', 'EnRoute', 'Arrived', 'GateIn', 'Completed', 'Cancelled', 'NoShow', 'RejectedAtGate']) {
      expect(texts['en']!.containsKey('status.$status'), isTrue, reason: status);
    }
    for (final light in ['Green', 'Amber', 'Red', 'Grey']) {
      expect(texts['en']!.containsKey('light.$light'), isTrue, reason: light);
    }
  });
}
