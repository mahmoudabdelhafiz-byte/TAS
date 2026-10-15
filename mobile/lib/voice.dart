// Spoken directions inside the terminal, so the driver needn't look at the phone while driving.

import 'package:flutter_tts/flutter_tts.dart';

import 'strings.dart';
import 'yard.dart';

/// Phone voices to try for each app language, best first, with the language of the text that voice reads.
/// Few phones have an Urdu voice; spoken Hindi is understood by Urdu speakers, so the Hindi voice reading the Hindi
/// text comes next.
const speechVoices = <String, List<(String, String)>>{
  'ar': [('ar-SA', 'ar'), ('ar-AE', 'ar'), ('ar', 'ar')],
  'en': [('en-GB', 'en'), ('en-US', 'en'), ('en', 'en')],
  'ur': [('ur-PK', 'ur'), ('ur-IN', 'ur'), ('ur', 'ur'), ('hi-IN', 'hi')],
  'hi': [('hi-IN', 'hi'), ('hi', 'hi')],
};

const _sentenceEnd = {'ar': '. ', 'en': '. ', 'ur': '۔ ', 'hi': '। '};

/// Rounded for speech: "about 650 metres", not "647.3".
int spokenMeters(double meters) => meters < 100 ? (meters / 10).round() * 10 : (meters / 50).round() * 50;

/// What is said for a stop: the move, the block, row / bay / tier, how far.
String spokenStop(String lang, YardStop stop, {double? meters}) {
  final parts = <String>[];
  if (stop.moved) parts.add(tr(lang, 'moved'));
  parts.add(switch (stop.kind) {
    'EXIT' => tr(lang, 'exitStop'),
    _ when stop.block == null => tr(lang, 'noPosition'),
    'DROP' => tr(lang, 'dropAt', {'block': _spell(stop.block!)}),
    _ => tr(lang, 'pickupAt', {'block': _spell(stop.block!)}),
  });
  final slot = stop.slotParts;
  if (slot.length >= 3) parts.add(tr(lang, 'rowBayTier', {'row': _number(slot[0]), 'bay': _number(slot[1]), 'tier': _number(slot[2])}));
  if (meters != null && meters >= 30) parts.add(tr(lang, 'speakMeters', {'m': '${spokenMeters(meters)}'}));
  final end = _sentenceEnd[lang] ?? '. ';
  return parts.map((p) => p.trim()).join(end).trim();
}

/// "B2" → "B 2" so the voice says the letter, then the number.
String _spell(String block) => block.replaceAllMapped(RegExp(r'(?<=[A-Za-z])(?=\d)|(?<=\d)(?=[A-Za-z])'), (_) => ' ');

/// "03" → "3": voices read leading zeros oddly.
String _number(String s) => int.tryParse(s)?.toString() ?? s;

/// The phone's text-to-speech in the driver's language.
class Voice {
  final FlutterTts _tts = FlutterTts();
  String? _appLang;
  String? _textLang; // null: no voice for this language on the phone

  /// Says the stop; silent when the phone has no voice for the driver's language.
  Future<void> sayStop(String lang, YardStop stop, {double? meters}) async {
    try {
      if (_appLang != lang) {
        _textLang = await _pickVoice(lang);
        _appLang = lang;
      }
      final textLang = _textLang;
      if (textLang == null) return;
      await _tts.stop();
      await _tts.speak(spokenStop(textLang, stop, meters: meters));
    } catch (_) {
      // No speech engine on this phone: the directions stay on screen.
    }
  }

  Future<void> stop() async {
    try {
      await _tts.stop();
    } catch (_) {}
  }

  Future<String?> _pickVoice(String lang) async {
    for (final (locale, textLang) in speechVoices[lang] ?? const [('en-US', 'en')]) {
      final ok = await _tts.isLanguageAvailable(locale);
      if (ok == true || ok == 1) {
        await _tts.setLanguage(locale);
        await _tts.setSpeechRate(0.45);
        return textLang;
      }
    }
    // Reading the text in a voice of another language is worse than silence.
    return null;
  }
}
