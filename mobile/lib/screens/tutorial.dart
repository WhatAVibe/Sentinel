import 'package:flutter/material.dart';

import '../core/store.dart';
import '../theme.dart';
import 'package:provider/provider.dart';

import '../core/app_state.dart';

class _Step {
  final IconData icon;
  final String title;
  final String body;
  const _Step(this.icon, this.title, this.body);
}

const _steps = [
  _Step(Icons.grid_view_rounded, 'Dashboard',
      'The live 2FA code sits under the PC status. Tap it to send the code to the PC clipboard, long-press to copy it on this phone. Quick actions lock, screenshot, arm away mode and more.'),
  _Step(Icons.timeline_rounded, 'Activity',
      'Every event from the relay is here, with the device that triggered it. Filter by category, load older pages and see power or security events.'),
  _Step(Icons.photo_library_outlined, 'Screenshots',
      'Screenshots land on your relay. Open them full size, load older pages, and purge the relay copy when something sensitive was captured.'),
  _Step(Icons.shield_outlined, 'Security',
      'The 2FA code, secure lock, policy toggles and the recovery options. Unlinking a phone or changing security asks for a code.'),
  _Step(Icons.devices_rounded, 'Linked devices',
      'Devices you pair appear on the PC. The desktop can rename them, mark one as primary and confirm new devices with a code from the device.'),
];

Future<void> showSentinelTutorial(BuildContext context, {bool force = false}) async {
  final store = context.read<AppState>().store;
  if (!force && await store.tutorialDone()) return;
  if (!context.mounted) return;
  await showDialog<void>(
    context: context,
    barrierDismissible: false,
    builder: (dialogContext) => _TutorialDialog(store: store),
  );
}

class _TutorialDialog extends StatefulWidget {
  final SessionStore store;

  const _TutorialDialog({required this.store});

  @override
  State<_TutorialDialog> createState() => _TutorialDialogState();
}

class _TutorialDialogState extends State<_TutorialDialog> {
  int _index = 0;

  Future<void> _finish() async {
    await widget.store.setTutorialDone(true);
    if (mounted) Navigator.pop(context);
  }

  @override
  Widget build(BuildContext context) {
    final step = _steps[_index];
    final last = _index == _steps.length - 1;
    return AlertDialog(
      backgroundColor: C.surface,
      shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(20)),
      title: Row(
        children: [
          Icon(step.icon, color: C.accent, size: 20),
          const SizedBox(width: 10),
          Expanded(
            child: Text(step.title,
                style: const TextStyle(fontSize: 17, fontWeight: FontWeight.w600)),
          ),
        ],
      ),
      content: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(step.body, style: const TextStyle(fontSize: 13, color: C.muted, height: 1.5)),
          const SizedBox(height: 18),
          Row(
            children: [
              for (var i = 0; i < _steps.length; i++)
                Container(
                  width: i == _index ? 18 : 6,
                  height: 6,
                  margin: const EdgeInsets.only(right: 5),
                  decoration: BoxDecoration(
                    color: i == _index ? C.accent : C.border,
                    borderRadius: BorderRadius.circular(3),
                  ),
                ),
              const Spacer(),
              Text('${_index + 1} / ${_steps.length}',
                  style: const TextStyle(fontSize: 10, color: C.muted)),
            ],
          ),
        ],
      ),
      actions: [
        TextButton(
          onPressed: _finish,
          child: const Text('Skip', style: TextStyle(color: C.muted)),
        ),
        if (_index > 0)
          TextButton(
            onPressed: () => setState(() => _index--),
            child: const Text('Back'),
          ),
        FilledButton(
          onPressed: () {
            if (last) {
              _finish();
            } else {
              setState(() => _index++);
            }
          },
          child: Text(last ? 'Done' : 'Next'),
        ),
      ],
    );
  }
}
