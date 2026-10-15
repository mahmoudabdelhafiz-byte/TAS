import 'package:flutter/material.dart';

// The Yardsyncs palette, same as the web portal.
const ink = Color(0xFF0F2A3A);
const page = Color(0xFFF2F5F7);
const line = Color(0xFFC9D3DA);
const teal = Color(0xFF0E7C86);
const muted = Color(0xFF5B6B77);
const green = Color(0xFF1E8E3E);
const amber = Color(0xFFD9950B);
const red = Color(0xFFC2302A);
const grey = Color(0xFF8796A3);

Color lightColor(String light) => switch (light) {
      'Green' => green,
      'Amber' => amber,
      'Red' => red,
      _ => grey,
    };

ThemeData yardsyncsTheme() => ThemeData(
      useMaterial3: true,
      colorScheme: ColorScheme.fromSeed(seedColor: teal, primary: teal, surface: Colors.white),
      scaffoldBackgroundColor: page,
      appBarTheme: const AppBarTheme(backgroundColor: ink, foregroundColor: Colors.white, elevation: 0),
      filledButtonTheme: FilledButtonThemeData(
        style: FilledButton.styleFrom(
          backgroundColor: teal,
          minimumSize: const Size.fromHeight(56),
          textStyle: const TextStyle(fontSize: 18, fontWeight: FontWeight.w600),
          shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(6)),
        ),
      ),
      inputDecorationTheme: const InputDecorationTheme(
        filled: true,
        fillColor: Colors.white,
        border: OutlineInputBorder(borderSide: BorderSide(color: line)),
      ),
      cardTheme: CardThemeData(
        color: Colors.white,
        elevation: 0,
        margin: EdgeInsets.zero,
        shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(6), side: const BorderSide(color: line)),
      ),
    );
