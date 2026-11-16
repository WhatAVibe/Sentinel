import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../core/app_state.dart';
import '../theme.dart';
import '../widgets.dart';
import 'activity_page.dart';
import 'gallery_page.dart';
import 'home_page.dart';
import 'security_page.dart';
import 'settings_page.dart';

class Shell extends StatefulWidget {
  const Shell({super.key});

  @override
  State<Shell> createState() => _ShellState();
}

class _ShellState extends State<Shell> {
  int _index = 0;

  static const _titles = ['Dashboard', 'Activity', 'Screenshots', 'Security'];

  @override
  Widget build(BuildContext context) {
    final state = context.watch<AppState>();
    final away = state.device?.away ?? false;

    return Scaffold(
      appBar: AppBar(
        titleSpacing: 16,
        title: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(_titles[_index], style: const TextStyle(fontSize: 18, fontWeight: FontWeight.w600)),
            Text(
              state.device?.name ?? state.session?.deviceName ?? 'Sentinel',
              style: const TextStyle(fontSize: 11, color: C.muted),
            ),
          ],
        ),
        actions: [
          if (away)
            const Padding(
              padding: EdgeInsets.only(right: 6),
              child: Pill('AWAY', color: C.warning, icon: Icons.nightlight_round),
            ),
          Padding(
            padding: const EdgeInsets.only(right: 4),
            child: ConnectionPill(state: state),
          ),
          IconButton(
            onPressed: () => Navigator.of(context).push(
              MaterialPageRoute(builder: (_) => const SettingsPage()),
            ),
            icon: const Icon(Icons.settings_outlined, size: 20, color: C.muted),
          ),
          const SizedBox(width: 4),
        ],
      ),
      body: IndexedStack(
        index: _index,
        children: const [
          HomePage(),
          ActivityPage(),
          GalleryPage(),
          SecurityPage(),
        ],
      ),
      bottomNavigationBar: BottomNavigationBar(
        currentIndex: _index,
        onTap: (value) => setState(() => _index = value),
        items: const [
          BottomNavigationBarItem(icon: Icon(Icons.grid_view_rounded), label: 'Home'),
          BottomNavigationBarItem(icon: Icon(Icons.timeline_rounded), label: 'Activity'),
          BottomNavigationBarItem(icon: Icon(Icons.photo_library_outlined), label: 'Shots'),
          BottomNavigationBarItem(icon: Icon(Icons.shield_outlined), label: 'Security'),
        ],
      ),
    );
  }
}
