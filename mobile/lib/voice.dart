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

/// What to do at a turn: "Turn left at block A1" ([speech] spells block codes for the voice).
String turnWhat(String lang, YardTurn turn, {bool speech = false}) {
  var what = tr(lang, 'turn.${turn.turn}');
  final near = turn.near;
  if (near != null) {
    final place = turn.nearIsBlock ? tr(lang, 'nearBlock', {'block': speech ? _spell(near) : near}) : near;
    what = tr(lang, 'turnNear', {'turn': what, 'place': place});
  }
  return _capital(what);
}

/// What is said for a turn: "In 50 metres, turn left at block A1" when [meters] is given, else "Now turn left at block A1".
String turnText(String lang, YardTurn turn, {double? meters, bool speech = false}) {
  final what = turnWhat(lang, turn, speech: speech);
  final lower = what.isEmpty ? what : what[0].toLowerCase() + what.substring(1);
  return _capital(meters == null ? tr(lang, 'turnNow', {'turn': lower}) : tr(lang, 'turnIn', {'m': '${spokenMeters(meters)}', 'turn': lower}));
}

String _capital(String s) => s.isEmpty ? s : s[0].toUpperCase() + s.substring(1);

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

  /// Says a turn ahead ([meters] away) or now.
  Future<void> sayTurn(String lang, YardTurn turn, {double? meters}) async {
    try {
      if (_appLang != lang) {
        _textLang = await _pickVoice(lang);
        _appLang = lang;
      }
      final textLang = _textLang;
      if (textLang == null) return;
      await _tts.stop();
      await _tts.speak(turnText(textLang, turn, meters: meters, speech: true));
    } catch (_) {}
  }

  /// Says one of the app's texts (by key) in the driver's language.
  Future<void> sayText(String lang, String key) async {
    try {
      if (_appLang != lang) {
        _textLang = await _pickVoice(lang);
        _appLang = lang;
      }
      final textLang = _textLang;
      if (textLang == null) return;
      await _tts.stop();
      await _tts.speak(tr(textLang, key));
    } catch (_) {}
  }

  Future<void> stop() async {
    try {
      await _tts.stop();
    } catch (_) {}
  }

  Future<String?> _pickVoice(String lang) async {
    // A language from a pack: its own voice if the phone has one, else English.
    for (final (locale, textLang) in speechVoices[lang] ?? [(lang, lang), ('en-US', 'en')]) {
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
