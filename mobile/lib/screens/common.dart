import 'package:flutter/material.dart';

import '../strings.dart';
import '../theme.dart';

/// Three boxes, one placed — the Yardsyncs mark.
class BrandMark extends StatelessWidget {
  const BrandMark({super.key, this.size = 28});

  final double size;

  @override
  Widget build(BuildContext context) {
    final box = size * 10 / 32;
    Widget square(Color color) => Container(width: box, height: box, decoration: BoxDecoration(color: color, borderRadius: BorderRadius.circular(1)));
    return SizedBox(
      width: size,
      height: size,
      child: Stack(children: [
        Positioned(left: size * 5 / 32, top: size * 17 / 32, child: square(teal)),
        Positioned(left: size * 17 / 32, top: size * 17 / 32, child: square(line)),
        Positioned(left: size * 17 / 32, top: size * 5 / 32, child: square(teal)),
      ]),
    );
  }
}

class LanguageButton extends StatelessWidget {
  const LanguageButton({super.key, required this.lang, required this.onLanguage, this.color = ink});

  final String lang;
  final ValueChanged<String> onLanguage;
  final Color color;

  @override
  Widget build(BuildContext context) {
    return PopupMenuButton<String>(
      tooltip: tr(lang, 'language'),
      onSelected: onLanguage,
      itemBuilder: (_) => [
        for (final code in supportedLanguages)
          CheckedPopupMenuItem(value: code, checked: code == lang, child: Text(languageNames[code]!)),
      ],
      child: Padding(
        padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 10),
        child: Row(mainAxisSize: MainAxisSize.min, children: [
          Icon(Icons.translate, size: 20, color: color),
          const SizedBox(width: 6),
          Text(languageNames[lang]!, style: TextStyle(color: color, fontSize: 15)),
        ]),
      ),
    );
  }
}

class ErrorBox extends StatelessWidget {
  const ErrorBox({super.key, required this.message});

  final String message;

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.all(14),
      decoration: BoxDecoration(
        color: const Color(0xFFF9E6E5),
        borderRadius: BorderRadius.circular(6),
        border: const BorderDirectional(start: BorderSide(color: red, width: 4)),
      ),
      child: Text(message, style: const TextStyle(fontSize: 16, color: ink)),
    );
  }
}

/// The traffic light: a dot and what it means, never the colour alone.
class LightBadge extends StatelessWidget {
  const LightBadge({super.key, required this.light, required this.lang, this.large = false});

  final String light;
  final String lang;
  final bool large;

  @override
  Widget build(BuildContext context) {
    final color = lightColor(light);
    final size = large ? 18.0 : 12.0;
    return Row(mainAxisSize: MainAxisSize.min, children: [
      Container(width: size, height: size, decoration: BoxDecoration(color: color, shape: BoxShape.circle)),
      SizedBox(width: large ? 10 : 7),
      Text(tr(lang, 'light.$light'),
          style: TextStyle(fontSize: large ? 20 : 15, fontWeight: FontWeight.w600, color: light == 'Red' ? red : ink)),
    ]);
  }
}
