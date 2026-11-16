import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../core/app_state.dart';
import '../models.dart';
import '../theme.dart';
import '../widgets.dart';

class HomePage extends StatelessWidget {
  const HomePage({super.key});

  Future<void> _send(
    BuildContext context,
    String type, {
    String? confirmTitle,
    String? confirmMessage,
    bool danger = false,
    Map<String, dynamic>? args,
    String? success,
    String? codeTitle,
  }) async {
    final state = context.read<AppState>();
    if (state.busy) return;

    if (confirmTitle != null) {
      final ok = await confirmAction(
        context,
        title: confirmTitle,
        message: confirmMessage ?? '',
        confirmLabel: 'Yes, continue',
        danger: danger,
      );
      if (!ok) return;
    }

    String? code;
    final needsCode = type == 'unlock' ||
        type == 'away_disable' ||
        (state.device?.protectPower == true && (type == 'shutdown' || type == 'restart'));
    if (needsCode) {
      code = await showCodeSheet(
        context,
        title: codeTitle ?? 'Confirm with your code',
        actionLabel: 'Send command',
      );
      if (code == null) return;
    }

    final result = await state.command(type, args: args, code: code);
    if (!context.mounted) return;
    ScaffoldMessenger.of(context).showSnackBar(
      SnackBar(
        content: Text(result.ok ? (success ?? 'Command delivered') : friendlyError(result.error)),
      ),
    );
  }

  Future<void> _disarm(BuildContext context) async {
    final code = await showCodeSheet(
      context,
      title: 'Disarm away mode',
      actionLabel: 'Unlock / disable',
    );
    if (code == null || !context.mounted) return;
    final state = context.read<AppState>();
    final result = await state.disarm(code);
    if (!context.mounted) return;
    ScaffoldMessenger.of(context).showSnackBar(
      SnackBar(content: Text(result.ok ? 'Away mode disabled' : friendlyError(result.error))),
    );
  }

  Future<void> _message(BuildContext context) async {
    final draft = await showModalBottomSheet<MessageDraft>(
      context: context,
      isScrollControlled: true,
      backgroundColor: C.surface,
      shape: const RoundedRectangleBorder(
        borderRadius: BorderRadius.vertical(top: Radius.circular(22)),
      ),
      builder: (context) => Padding(
        padding: EdgeInsets.only(bottom: MediaQuery.of(context).viewInsets.bottom),
        child: const _MessageComposer(),
      ),
    );
    if (draft == null || !context.mounted) return;
    final state = context.read<AppState>();
    final result = await state.sendMessage(
      body: draft.body,
      title: draft.title,
      kind: draft.kind,
      allowReply: draft.allowReply,
    );
    if (!context.mounted) return;
    ScaffoldMessenger.of(context).showSnackBar(
      SnackBar(
        content: Text(result.ok
            ? (draft.allowReply ? 'Message shown — waiting for a reply' : 'Message shown on the PC')
            : friendlyError(result.error)),
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final state = context.watch<AppState>();
    final device = state.device;

    return RefreshIndicator(
      onRefresh: state.refreshDevice,
      color: C.accent,
      backgroundColor: C.surface,
      child: ListView(
        padding: const EdgeInsets.fromLTRB(16, 8, 16, 28),
        children: [
          if (device == null)
            const CardBox(
              child: SizedBox(
                height: 120,
                child: Center(child: CircularProgressIndicator()),
              ),
            )
          else
            _Hero(device: device, state: state),
          if (state.lastError != null) ...[
            const SizedBox(height: 10),
            Container(
              padding: const EdgeInsets.all(12),
              decoration: BoxDecoration(
                color: C.warning.withValues(alpha: 0.08),
                borderRadius: BorderRadius.circular(12),
                border: Border.all(color: C.warning.withValues(alpha: 0.3)),
              ),
              child: Row(
                children: [
                  const Icon(Icons.wifi_off_rounded, size: 16, color: C.warning),
                  const SizedBox(width: 8),
                  Expanded(
                    child: Text(state.lastError!,
                        style: const TextStyle(fontSize: 11, color: C.warning)),
                  ),
                ],
              ),
            ),
          ],
          if (device?.away == true) ...[
            const SizedBox(height: 10),
            _AwayBanner(onDisarm: () => _disarm(context)),
          ],
          const SectionHeader('Quick actions'),
          GridView.count(
            crossAxisCount: 2,
            shrinkWrap: true,
            physics: const NeverScrollableScrollPhysics(),
            childAspectRatio: 1.75,
            mainAxisSpacing: 10,
            crossAxisSpacing: 10,
            children: [
              ActionTile(
                icon: Icons.lock_rounded,
                title: 'Lock PC',
                subtitle: 'Workstation lock',
                onTap: () => _send(context, 'lock', success: 'PC locked'),
              ),
              ActionTile(
                icon: Icons.photo_camera_rounded,
                title: 'Screenshot',
                subtitle: 'All displays',
                color: const Color(0xFF5BC0EB),
                onTap: () => _send(context, 'screenshot', success: 'Screenshots captured'),
              ),
              ActionTile(
                icon: Icons.nightlight_round,
                title: device?.away == true ? 'Away (active)' : 'Away mode',
                subtitle: device?.away == true ? 'Screensaver + logging' : 'Arm monitoring',
                color: C.warning,
                onTap: device?.away == true
                    ? () => _disarm(context)
                    : () => _send(context, 'away_enable', success: 'Away mode armed'),
              ),
              ActionTile(
                icon: Icons.monitor_rounded,
                title: 'Display off',
                subtitle: 'Turn monitors off',
                onTap: () => _send(context, 'screen_off', success: 'Displays off'),
              ),
              ActionTile(
                icon: Icons.chat_bubble_outline_rounded,
                title: 'Message',
                subtitle: 'Show text on PC',
                onTap: () => _message(context),
              ),
              ActionTile(
                icon: Icons.bedtime_rounded,
                title: 'Sleep',
                subtitle: 'Suspend PC',
                color: C.warning,
                onTap: () => _send(
                  context,
                  'sleep',
                  confirmTitle: 'Put the PC to sleep?',
                  confirmMessage: 'Remote wake depends on your BIOS / network settings.',
                ),
              ),
              ActionTile(
                icon: Icons.restart_alt_rounded,
                title: 'Restart',
                subtitle: '10s delay',
                color: C.warning,
                onTap: () => _send(
                  context,
                  'restart',
                  confirmTitle: 'Restart the PC?',
                  confirmMessage: 'A 10 second countdown starts immediately.',
                  success: 'Restart scheduled',
                ),
              ),
              ActionTile(
                icon: Icons.power_settings_new_rounded,
                title: 'Shut down',
                subtitle: '10s delay',
                color: C.danger,
                onTap: () => _send(
                  context,
                  'shutdown',
                  confirmTitle: 'Shut down the PC?',
                  confirmMessage: 'A 10 second countdown starts immediately.',
                  danger: true,
                  success: 'Shutdown scheduled',
                ),
              ),
            ],
          ),
          if (device?.away == true) ...[
            const SizedBox(height: 10),
            CardBox(
              color: C.elevated,
              padding: const EdgeInsets.all(16),
              child: Row(
                children: [
                  const Icon(Icons.shield_rounded, color: C.success, size: 20),
                  const SizedBox(width: 12),
                  const Expanded(
                    child: Text(
                      'Unlock / disable away mode',
                      style: TextStyle(fontSize: 13, fontWeight: FontWeight.w600),
                    ),
                  ),
                  FilledButton(
                    style: FilledButton.styleFrom(
                      backgroundColor: C.success.withValues(alpha: 0.16),
                      foregroundColor: C.success,
                      padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 10),
                    ),
                    onPressed: () => _disarm(context),
                    child: const Text('2FA'),
                  ),
                ],
              ),
            ),
          ],
          SectionHeader(
            'Recent activity',
            trailing: Text(
              state.socketLive ? 'live' : 'polling',
              style: TextStyle(
                fontSize: 11,
                color: state.socketLive ? C.success : C.muted,
              ),
            ),
          ),
          CardBox(
            padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 6),
            child: state.events.isEmpty
                ? const Padding(
                    padding: EdgeInsets.all(18),
                    child: Text('Nothing logged yet — arm away mode or take a screenshot.',
                        style: TextStyle(fontSize: 12, color: C.muted)),
                  )
                : Column(
                    children: [
                      for (final event in state.events.take(6))
                        EventTile(event: event, showDetail: true),
                    ],
                  ),
          ),
        ],
      ),
    );
  }
}

class _Hero extends StatelessWidget {
  final Device device;
  final AppState state;

  const _Hero({required this.device, required this.state});

  @override
  Widget build(BuildContext context) {
    return CardBox(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Expanded(
                child: Text(
                  device.name,
                  style: const TextStyle(fontSize: 20, fontWeight: FontWeight.w700, letterSpacing: -0.3),
                ),
              ),
              Pill(
                device.online ? 'ONLINE' : 'OFFLINE',
                color: device.online ? C.success : C.warning,
              ),
            ],
          ),
          const SizedBox(height: 4),
          Text(
            [device.metrics.user, device.metrics.hostname].where((e) => e.isNotEmpty).join('  ·  '),
            style: const TextStyle(fontSize: 12, color: C.muted),
          ),
          const SizedBox(height: 18),
          _Bar(label: 'CPU', value: device.metrics.cpuPercent / 100, right: '${device.metrics.cpuPercent.toStringAsFixed(0)}%'),
          const SizedBox(height: 10),
          _Bar(
            label: 'Memory',
            value: device.metrics.memPercent / 100,
            right: device.metrics.memText,
            color: C.success,
          ),
          const SizedBox(height: 18),
          Wrap(
            spacing: 8,
            runSpacing: 8,
            children: [
              StatChip(label: 'Displays', value: '${device.state.monitors}'),
              StatChip(label: 'Session', value: device.locked ? 'locked' : device.state.session),
              StatChip(label: 'Uptime', value: device.metrics.uptimeText),
              StatChip(label: 'Last beat', value: formatRelative(device.lastSeen)),
            ],
          ),
        ],
      ),
    );
  }
}

class _Bar extends StatelessWidget {
  final String label;
  final double value;
  final String right;
  final Color color;

  const _Bar({required this.label, required this.value, required this.right, this.color = C.accent});

  @override
  Widget build(BuildContext context) {
    return Column(
      children: [
        Row(
          children: [
            Text(label, style: const TextStyle(fontSize: 11, color: C.muted)),
            const Spacer(),
            Text(right, style: const TextStyle(fontSize: 11, color: C.muted)),
          ],
        ),
        const SizedBox(height: 5),
        ClipRRect(
          borderRadius: BorderRadius.circular(4),
          child: LinearProgressIndicator(
            value: value.clamp(0.0, 1.0),
            minHeight: 6,
            backgroundColor: C.elevated,
            valueColor: AlwaysStoppedAnimation<Color>(color),
          ),
        ),
      ],
    );
  }
}

class _AwayBanner extends StatelessWidget {
  final VoidCallback onDisarm;

  const _AwayBanner({required this.onDisarm});
  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.all(16),
      decoration: BoxDecoration(
        color: C.warning.withValues(alpha: 0.08),
        borderRadius: BorderRadius.circular(16),
        border: Border.all(color: C.warning.withValues(alpha: 0.3)),
      ),
      child: Row(
        children: [
          const Icon(Icons.nightlight_round, color: C.warning, size: 22),
          const SizedBox(width: 12),
          const Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text('Away mode active',
                    style: TextStyle(fontSize: 14, fontWeight: FontWeight.w600, color: C.warning)),
                SizedBox(height: 3),
                Text('Screensaver engaged · events are being recorded',
                    style: TextStyle(fontSize: 11, color: C.muted)),
              ],
            ),
          ),
          TextButton(
            onPressed: onDisarm,
            style: TextButton.styleFrom(foregroundColor: C.warning),
            child: const Text('Disarm'),
          ),
        ],
      ),
    );
  }
}

class MessageDraft {
  final String title;
  final String body;
  final String kind;
  final bool allowReply;

  const MessageDraft({
    required this.title,
    required this.body,
    required this.kind,
    required this.allowReply,
  });
}

class _MessageComposer extends StatefulWidget {
  const _MessageComposer();

  @override
  State<_MessageComposer> createState() => _MessageComposerState();
}

class _MessageComposerState extends State<_MessageComposer> {
  final TextEditingController _title = TextEditingController(text: 'Message from your phone');
  final TextEditingController _body = TextEditingController();
  String _kind = 'dialog';
  bool _allowReply = true;

  static const _kinds = [
    ('toast', 'Toast', 'Auto-closes'),
    ('dialog', 'Dialog', 'Waits for the PC'),
    ('alert', 'Alert', 'Red, urgent'),
    ('banner', 'Banner', 'Amber notice'),
    ('fullscreen', 'Fullscreen', 'Forced overlay'),
  ];

  @override
  void dispose() {
    _title.dispose();
    _body.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.fromLTRB(20, 14, 20, 24),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Center(
            child: Container(
              width: 36,
              height: 4,
              decoration: BoxDecoration(
                color: C.border,
                borderRadius: BorderRadius.circular(2),
              ),
            ),
          ),
          const SizedBox(height: 16),
          const Text('Send a message to the PC',
              style: TextStyle(fontSize: 16, fontWeight: FontWeight.w600)),
          const SizedBox(height: 4),
          const Text('It appears on the PC screen, optionally with a reply box.',
              style: TextStyle(fontSize: 11, color: C.muted)),
          const SizedBox(height: 14),
          TextField(
            controller: _title,
            decoration: const InputDecoration(labelText: 'Title'),
          ),
          const SizedBox(height: 10),
          TextField(
            controller: _body,
            minLines: 2,
            maxLines: 4,
            autofocus: true,
            decoration: const InputDecoration(
              hintText: 'Type the message...',
              alignLabelWithHint: true,
            ),
          ),
          const SizedBox(height: 14),
          Wrap(
            spacing: 8,
            runSpacing: 8,
            children: [
              for (final (kind, label, hint) in _kinds)
                GestureDetector(
                  onTap: () => setState(() => _kind = kind),
                  child: Container(
                    padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
                    decoration: BoxDecoration(
                      color: _kind == kind ? C.accentDim : C.elevated,
                      borderRadius: BorderRadius.circular(10),
                      border: Border.all(color: _kind == kind ? C.accent : C.border),
                    ),
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text(label,
                            style: const TextStyle(fontSize: 12, fontWeight: FontWeight.w600)),
                        Text(hint, style: const TextStyle(fontSize: 9, color: C.muted)),
                      ],
                    ),
                  ),
                ),
            ],
          ),
          const SizedBox(height: 8),
          SwitchListTile(
            contentPadding: EdgeInsets.zero,
            value: _allowReply,
            onChanged: (value) => setState(() => _allowReply = value),
            title: const Text('Allow a reply', style: TextStyle(fontSize: 13)),
            subtitle: const Text('The PC can type an answer back to this phone',
                style: TextStyle(fontSize: 11, color: C.muted)),
          ),
          const SizedBox(height: 10),
          SizedBox(
            width: double.infinity,
            child: FilledButton(
              onPressed: () {
                final body = _body.text.trim();
                if (body.isEmpty) return;
                Navigator.pop(
                  context,
                  MessageDraft(
                    title: _title.text.trim().isEmpty
                        ? 'Message from your phone'
                        : _title.text.trim(),
                    body: body,
                    kind: _kind,
                    allowReply: _allowReply,
                  ),
                );
              },
              child: const Text('Send to PC'),
            ),
          ),
        ],
      ),
    );
  }
}

