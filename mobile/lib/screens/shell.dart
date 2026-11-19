import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../core/app_state.dart';
import '../models.dart';
import '../theme.dart';
import '../widgets.dart';
import 'activity_page.dart';
import 'gallery_page.dart';
import 'home_page.dart';
import 'pair_screen.dart';
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

  void _showPcPicker(BuildContext context, AppState state) {
    showModalBottomSheet<void>(
      context: context,
      backgroundColor: C.surface,
      shape: const RoundedRectangleBorder(
        borderRadius: BorderRadius.vertical(top: Radius.circular(22)),
      ),
      builder: (sheetContext) => SafeArea(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            const ListTile(
              title: Text('Your PCs', style: TextStyle(fontSize: 15, fontWeight: FontWeight.w600)),
              subtitle: Text('Switch device, rename it locally, or pair another PC',
                  style: TextStyle(fontSize: 11, color: C.muted)),
            ),
            for (final pc in state.sessions)
              ListTile(
                leading: Icon(pc.isDirect ? Icons.usb_rounded : Icons.computer_rounded,
                    size: 20, color: pc.deviceId == state.session?.deviceId ? C.accent : C.muted),
                title: Text(pc.displayName, style: const TextStyle(fontSize: 14)),
                subtitle: Text(
                  pc.localName.isNotEmpty && pc.localName != pc.deviceName
                      ? pc.deviceName
                      : (pc.isDirect ? 'Direct ADB link' : (Uri.tryParse(pc.baseUrl)?.host ?? pc.baseUrl)),
                  style: const TextStyle(fontSize: 10.5, color: C.muted),
                ),
                trailing: Row(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    if (pc.deviceId == state.session?.deviceId)
                      const Icon(Icons.check_rounded, size: 17, color: C.success),
                    IconButton(
                      tooltip: 'Local name',
                      icon: const Icon(Icons.edit_outlined, size: 16, color: C.muted),
                      onPressed: () => _renamePc(sheetContext, state, pc),
                    ),
                  ],
                ),
                onTap: () {
                  Navigator.pop(sheetContext);
                  state.switchTo(pc);
                },
              ),
            const Divider(height: 1),
            ListTile(
              leading: const Icon(Icons.add_rounded, size: 20, color: C.accent),
              title: const Text('Pair another PC', style: TextStyle(fontSize: 14)),
              subtitle: const Text('Run the agent there and enter its pairing code',
                  style: TextStyle(fontSize: 10.5, color: C.muted)),
              onTap: () {
                Navigator.pop(sheetContext);
                Navigator.of(context).push(MaterialPageRoute(builder: (_) => const PairScreen()));
              },
            ),
          ],
        ),
      ),
    );
  }

  Future<void> _renamePc(BuildContext context, AppState state, Session pc) async {
    final controller = TextEditingController(text: pc.localName);
    final name = await showDialog<String>(
      context: context,
      builder: (dialogContext) => AlertDialog(
        backgroundColor: C.surface,
        title: const Text('Local name', style: TextStyle(fontSize: 16)),
        content: TextField(
          controller: controller,
          autofocus: true,
          decoration: InputDecoration(hintText: pc.deviceName),
        ),
        actions: [
          TextButton(onPressed: () => Navigator.pop(dialogContext), child: const Text('Cancel')),
          FilledButton(
            onPressed: () => Navigator.pop(dialogContext, controller.text),
            child: const Text('Save'),
          ),
        ],
      ),
    );
    controller.dispose();
    if (name != null) await state.renameSession(pc.deviceId, name);
  }

  @override
  Widget build(BuildContext context) {
    final state = context.watch<AppState>();
    final away = state.device?.away ?? false;

    return Scaffold(
      appBar: AppBar(
        titleSpacing: 16,
        title: InkWell(
          onTap: () => _showPcPicker(context, state),
          borderRadius: BorderRadius.circular(8),
          child: Padding(
            padding: const EdgeInsets.symmetric(vertical: 6, horizontal: 2),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(_titles[_index], style: const TextStyle(fontSize: 18, fontWeight: FontWeight.w600)),
                Row(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    Text(
                      state.session?.displayName ?? state.device?.name ?? 'Sentinel',
                      style: const TextStyle(fontSize: 11, color: C.muted),
                    ),
                    const SizedBox(width: 3),
                    const Icon(Icons.expand_more_rounded, size: 13, color: C.muted),
                  ],
                ),
              ],
            ),
          ),
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
