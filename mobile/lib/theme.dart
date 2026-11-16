import 'package:flutter/material.dart';

class C {
  static const bg = Color(0xFF0B0D11);
  static const surface = Color(0xFF12161C);
  static const elevated = Color(0xFF171C24);
  static const border = Color(0xFF232A35);
  static const accent = Color(0xFF6C8CFF);
  static const accentDim = Color(0xFF2A3560);
  static const text = Color(0xFFE6E9EF);
  static const muted = Color(0xFF8B93A1);
  static const success = Color(0xFF6EE7A8);
  static const warning = Color(0xFFF2C46D);
  static const danger = Color(0xFFFF6B6B);
}

ThemeData buildTheme() {
  const scheme = ColorScheme.dark(
    primary: C.accent,
    onPrimary: Colors.white,
    secondary: C.accent,
    surface: C.surface,
    onSurface: C.text,
    error: C.danger,
    outline: C.border,
  );

  return ThemeData(
    useMaterial3: true,
    brightness: Brightness.dark,
    colorScheme: scheme,
    scaffoldBackgroundColor: C.bg,
    canvasColor: C.bg,
    dividerColor: C.border,
    splashFactory: InkRipple.splashFactory,
    appBarTheme: const AppBarTheme(
      backgroundColor: C.bg,
      surfaceTintColor: Colors.transparent,
      elevation: 0,
      centerTitle: false,
      titleTextStyle: TextStyle(color: C.text, fontSize: 18, fontWeight: FontWeight.w600),
    ),
    textTheme: const TextTheme(
      headlineMedium: TextStyle(color: C.text, fontSize: 26, fontWeight: FontWeight.w600),
      titleLarge: TextStyle(color: C.text, fontSize: 18, fontWeight: FontWeight.w600),
      titleMedium: TextStyle(color: C.text, fontSize: 15, fontWeight: FontWeight.w600),
      bodyMedium: TextStyle(color: C.text, fontSize: 13),
      bodySmall: TextStyle(color: C.muted, fontSize: 12),
    ),
    cardTheme: CardThemeData(
      color: C.surface,
      elevation: 0,
      margin: EdgeInsets.zero,
      shape: RoundedRectangleBorder(
        borderRadius: BorderRadius.circular(18),
        side: const BorderSide(color: C.border),
      ),
    ),
    inputDecorationTheme: InputDecorationTheme(
      filled: true,
      fillColor: const Color(0xFF0E1218),
      contentPadding: const EdgeInsets.symmetric(horizontal: 14, vertical: 14),
      hintStyle: const TextStyle(color: C.muted, fontSize: 13),
      enabledBorder: OutlineInputBorder(
        borderRadius: BorderRadius.circular(12),
        borderSide: const BorderSide(color: C.border),
      ),
      focusedBorder: OutlineInputBorder(
        borderRadius: BorderRadius.circular(12),
        borderSide: const BorderSide(color: C.accent, width: 1.4),
      ),
      errorBorder: OutlineInputBorder(
        borderRadius: BorderRadius.circular(12),
        borderSide: const BorderSide(color: C.danger),
      ),
    ),
    filledButtonTheme: FilledButtonThemeData(
      style: FilledButton.styleFrom(
        backgroundColor: C.accent,
        foregroundColor: Colors.white,
        padding: const EdgeInsets.symmetric(horizontal: 18, vertical: 14),
        shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(12)),
        textStyle: const TextStyle(fontSize: 14, fontWeight: FontWeight.w600),
      ),
    ),
    outlinedButtonTheme: OutlinedButtonThemeData(
      style: OutlinedButton.styleFrom(
        foregroundColor: C.text,
        padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 13),
        side: const BorderSide(color: C.border),
        shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(12)),
      ),
    ),
    textButtonTheme: TextButtonThemeData(
      style: TextButton.styleFrom(foregroundColor: C.accent),
    ),
    snackBarTheme: SnackBarThemeData(
      backgroundColor: C.elevated,
      contentTextStyle: const TextStyle(color: C.text, fontSize: 13),
      behavior: SnackBarBehavior.floating,
      shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(12)),
    ),
    bottomNavigationBarTheme: const BottomNavigationBarThemeData(
      backgroundColor: C.surface,
      selectedItemColor: C.accent,
      unselectedItemColor: C.muted,
      type: BottomNavigationBarType.fixed,
      elevation: 0,
    ),
    switchTheme: SwitchThemeData(
      thumbColor: WidgetStateProperty.resolveWith((s) => s.contains(WidgetState.selected) ? Colors.white : C.muted),
      trackColor: WidgetStateProperty.resolveWith((s) => s.contains(WidgetState.selected) ? C.accent : C.elevated),
      trackOutlineColor: const WidgetStatePropertyAll(C.border),
    ),
    progressIndicatorTheme: const ProgressIndicatorThemeData(color: C.accent),
    listTileTheme: const ListTileThemeData(iconColor: C.muted, textColor: C.text),
  );
}

Color categoryColor(String category) {
  switch (category) {
    case 'window':
      return const Color(0xFF6C8CFF);
    case 'process':
      return const Color(0xFFA78BFA);
    case 'usb':
      return const Color(0xFFF2C46D);
    case 'clipboard':
      return const Color(0xFF6EE7A8);
    case 'session':
      return const Color(0xFFFF9F6B);
    case 'security':
      return const Color(0xFFFF6B6B);
    case 'screenshot':
      return const Color(0xFF5BC0EB);
    case 'power':
      return const Color(0xFFF2A65A);
    default:
      return const Color(0xFF8B93A1);
  }
}

IconData categoryIcon(String category) {
  switch (category) {
    case 'window':
      return Icons.window_rounded;
    case 'process':
      return Icons.memory_rounded;
    case 'usb':
      return Icons.usb_rounded;
    case 'clipboard':
      return Icons.content_paste_rounded;
    case 'session':
      return Icons.lock_clock_rounded;
    case 'security':
      return Icons.shield_rounded;
    case 'screenshot':
      return Icons.photo_camera_rounded;
    case 'power':
      return Icons.power_settings_new_rounded;
    default:
      return Icons.bolt_rounded;
  }
}
