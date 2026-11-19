import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:provider/provider.dart';

import '../core/app_state.dart';
import '../core/totp.dart';
import '../models.dart';
import '../theme.dart';
import '../widgets.dart';

class SecurityPage extends StatefulWidget {
  const SecurityPage({super.key});

  @override
  State<SecurityPage> createState() => _SecurityPageState();
}

class _SecurityPageState extends State<SecurityPage> {
  late final Timer _timer;
  int _seconds = totpSecondsRemaining();
  bool _verifying = false;
  String? _verification;
  final TextEditingController _lockMessage =
      TextEditingController(text: 'This PC is locked from Sentinel.');

  @override
  void initState() {
    super.initState();
    _timer = Timer.periodic(const Duration(seconds: 1), (_) {
      if (mounted) setState(() => _seconds = totpSecondsRemaining());
    });
  }

  @override
  void dispose() {
    _timer.cancel();
    _lockMessage.dispose();
    super.dispose();
  }

  Future<void> _secureLock() async {
    final state = context.read<AppState>();
    final message = _lockMessage.text.trim().isEmpty
        ? 'This PC is locked from Sentinel.'
        : _lockMessage.text.trim();
    final result = await state.command('lock_screen', args: {'message': message});
    if (!mounted) return;
    ScaffoldMessenger.of(context).showSnackBar(
      SnackBar(content: Text(result.ok ? 'PC locked with 2FA' : friendlyError(result.error))),
    );
  }

  Future<void> _unlockScreen() async {
    final code = await showCodeSheet(
      context,
      title: 'Unlock the PC screen',
      actionLabel: 'Unlock screen',
    );
    if (code == null || !mounted) return;
    final state = context.read<AppState>();
    final result = await state.command('unlock_screen', code: code);
    if (!mounted) return;
    ScaffoldMessenger.of(context).showSnackBar(
      SnackBar(content: Text(result.ok ? 'Screen unlocked' : friendlyError(result.error))),
    );
  }

  Future<void> _copy(String code) async {
    await Clipboard.setData(ClipboardData(text: code));
    if (!mounted) return;
    ScaffoldMessenger.of(context).showSnackBar(const SnackBar(content: Text('Code copied')));
  }

  Future<void> _verify(String code) async {
    final state = context.read<AppState>();
    setState(() {
      _verifying = true;
      _verification = null;
    });
    final ok = await state.verifyCode(code);
    if (!mounted) return;
    setState(() {
      _verifying = false;
      _verification = ok ? 'Verified by the server' : 'Server rejected this code';
    });
  }

  void _showPresenceHelp(BuildContext context) {
    showDialog<void>(
      context: context,
      builder: (dialogContext) => AlertDialog(
        backgroundColor: C.surface,
        title: const Text('Link ADB presence', style: TextStyle(fontSize: 16)),
        content: const SingleChildScrollView(
          child: Text(
            'Presence is a direct ADB link between this phone and the PC. Relay pairing alone does not '
            'count.\n\n'
            '1. Phone: Settings → About phone → tap Build number seven times.\n'
            '2. Developer options → Wireless debugging → Pair device with pairing code.\n'
            '3. PC: Sentinel → Settings → Direct link → Enable, then enter the ip:port and code.\n'
            '4. Keep wireless debugging on; this screen will show "linked".\n\n'
            'Only required when the PC security policy requires a trusted phone to disarm.',
            style: TextStyle(fontSize: 12, height: 1.4),
          ),
        ),
        actions: [
          TextButton(onPressed: () => Navigator.pop(dialogContext), child: const Text('Got it')),
        ],
      ),
    );
  }

  Future<void> _disarm() async {
    final code = await showCodeSheet(
      context,
      title: 'Disarm away mode',
      actionLabel: 'Unlock / disable',
    );
    if (code == null || !mounted) return;
    final state = context.read<AppState>();
    final result = await state.disarm(code);
    if (!mounted) return;
    ScaffoldMessenger.of(context).showSnackBar(
      SnackBar(content: Text(result.ok ? 'Away mode disabled' : friendlyError(result.error))),
    );
  }

  @override
  Widget build(BuildContext context) {
    final state = context.watch<AppState>();
    final code = state.liveCode;
    final device = state.device;
    final direct = state.session?.isDirect ?? false;

    return ListView(
      padding: const EdgeInsets.fromLTRB(16, 10, 16, 28),
      children: [
        CardBox(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Row(
                children: [
                  const Icon(Icons.shield_rounded, color: C.accent, size: 18),
                  const SizedBox(width: 8),
                  const Text('Second factor',
                      style: TextStyle(fontSize: 14, fontWeight: FontWeight.w600)),
                  const Spacer(),
                  Container(
                    width: 44,
                    height: 44,
                    alignment: Alignment.center,
                    child: Stack(
                      alignment: Alignment.center,
                      children: [
                        SizedBox(
                          width: 40,
                          height: 40,
                          child: CircularProgressIndicator(
                            value: _seconds / 30,
                            strokeWidth: 3,
                            backgroundColor: C.elevated,
                          ),
                        ),
                        Text('$_seconds',
                            style: const TextStyle(fontSize: 11, fontWeight: FontWeight.w600)),
                      ],
                    ),
                  ),
                ],
              ),
              const SizedBox(height: 8),
              const Text(
                'Used when you unlock or disable away mode. It works offline, and it matches the code the PC expects.',
                style: TextStyle(fontSize: 11, color: C.muted, height: 1.5),
              ),
              const SizedBox(height: 14),
              Container(
                width: double.infinity,
                padding: const EdgeInsets.symmetric(vertical: 20),
                decoration: BoxDecoration(
                  color: C.elevated,
                  borderRadius: BorderRadius.circular(14),
                  border: Border.all(color: C.border),
                ),
                child: Text(
                  formatCode(code.isEmpty ? '------' : code),
                  textAlign: TextAlign.center,
                  style: const TextStyle(
                    fontSize: 34,
                    fontWeight: FontWeight.w700,
                    letterSpacing: 6,
                    fontFeatures: [FontFeature.tabularFigures()],
                  ),
                ),
              ),
              const SizedBox(height: 12),
              Row(
                children: [
                  Expanded(
                    child: OutlinedButton.icon(
                      onPressed: code.isEmpty ? null : () => _copy(code),
                      icon: const Icon(Icons.copy_rounded, size: 16),
                      label: const Text('Copy'),
                    ),
                  ),
                  const SizedBox(width: 10),
                  Expanded(
                    child: OutlinedButton.icon(
                      onPressed: _verifying || code.isEmpty ? null : () => _verify(code),
                      icon: _verifying
                          ? const SizedBox(
                              width: 14, height: 14, child: CircularProgressIndicator(strokeWidth: 2))
                          : const Icon(Icons.cloud_done_outlined, size: 16),
                      label: const Text('Verify'),
                    ),
                  ),
                ],
              ),
              if (_verification != null) ...[
                const SizedBox(height: 10),
                Text(
                  _verification!,
                  style: TextStyle(
                    fontSize: 11,
                    color: _verification!.startsWith('Verified') ? C.success : C.danger,
                  ),
                ),
              ],
            ],
          ),
        ),
        const SizedBox(height: 12),
        CardBox(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Row(
                children: [
                  const Icon(Icons.lock_rounded, color: C.accent, size: 18),
                  const SizedBox(width: 8),
                  const Text('Secure lock',
                      style: TextStyle(fontSize: 14, fontWeight: FontWeight.w600)),
                  const Spacer(),
                  Pill(
                    device?.state.secureLock == true ? 'LOCKED' : 'READY',
                    color: device?.state.secureLock == true ? C.warning : C.muted,
                  ),
                ],
              ),
              const SizedBox(height: 8),
              const Text(
                'Covers every monitor with your wallpaper, blocks shortcuts and input, and needs the 2FA code to unlock.',
                style: TextStyle(fontSize: 11, color: C.muted, height: 1.5),
              ),
              if (device?.state.secureLock != true) ...[
                const SizedBox(height: 12),
                TextField(
                  controller: _lockMessage,
                  maxLength: 120,
                  decoration: const InputDecoration(
                    labelText: 'Message shown on the lock screen',
                    counterText: '',
                  ),
                ),
                const SizedBox(height: 10),
                SizedBox(
                  width: double.infinity,
                  child: FilledButton.icon(
                    onPressed: state.busy ? null : _secureLock,
                    icon: const Icon(Icons.lock_rounded, size: 18),
                    label: const Text('Lock this PC'),
                  ),
                ),
              ] else ...[
                const SizedBox(height: 12),
                SizedBox(
                  width: double.infinity,
                  child: FilledButton.icon(
                    style: FilledButton.styleFrom(
                      backgroundColor: C.success,
                      foregroundColor: Colors.black,
                    ),
                    onPressed: state.busy ? null : _unlockScreen,
                    icon: const Icon(Icons.lock_open_rounded, size: 18),
                    label: const Text('Unlock screen (2FA)'),
                  ),
                ),
                const SizedBox(height: 6),
                const Text(
                  'You can also type the code directly on the locked PC.',
                  style: TextStyle(fontSize: 10.5, color: C.muted),
                ),
              ],
            ],
          ),
        ),
        if (device?.away == true) ...[
          const SizedBox(height: 12),
          CardBox(
            color: C.warning.withValues(alpha: 0.07),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Row(
                  children: [
                    const Icon(Icons.nightlight_round, color: C.warning, size: 18),
                    const SizedBox(width: 8),
                    const Text('Away mode is active',
                        style: TextStyle(fontSize: 14, fontWeight: FontWeight.w600)),
                  ],
                ),
                const SizedBox(height: 6),
                const Text(
                  'The PC shows your screensaver and is recording activity. Disarming needs the code above.',
                  style: TextStyle(fontSize: 11, color: C.muted, height: 1.5),
                ),
                const SizedBox(height: 14),
                SizedBox(
                  width: double.infinity,
                  child: FilledButton.icon(
                    style: FilledButton.styleFrom(
                      backgroundColor: C.success,
                      foregroundColor: Colors.black,
                    ),
                    onPressed: state.busy ? null : _disarm,
                    icon: const Icon(Icons.lock_open_rounded, size: 18),
                    label: const Text('Unlock / disable away mode'),
                  ),
                ),
              ],
            ),
          ),
        ],
        const SectionHeader('Security policy'),
        CardBox(
          padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 8),
          child: Column(
            children: [
              _SwitchRow(
                title: 'Require code to unlock',
                subtitle: 'Protects unlock and away-mode disarm',
                value: device?.require2fa ?? true,
                onChanged: direct
                    ? null
                    : (value) async {
                        await state.updateSettings(require2fa: value);
                      },
              ),
              const Divider(color: C.border, height: 1),
              _SwitchRow(
                title: 'Protect power commands',
                subtitle: 'Also require a code for shutdown and restart',
                value: device?.protectPower ?? false,
                onChanged: direct
                    ? null
                    : (value) async {
                        await state.updateSettings(protectPower: value);
                      },
              ),
              if (direct) ...[
                const Divider(color: C.border, height: 1),
                const Padding(
                  padding: EdgeInsets.symmetric(vertical: 12),
                  child: Text(
                    'Direct-link mode uses the policy configured on the PC.',
                    style: TextStyle(fontSize: 11, color: C.muted),
                  ),
                ),
              ],
            ],
          ),
        ),
        const SectionHeader('This phone'),
        CardBox(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              _InfoRow(label: 'Server', value: state.session?.label ?? '—'),
              _InfoRow(label: 'Mode', value: direct ? 'Direct (ADB)' : 'Relay'),
              _InfoRow(
                label: 'Paired',
                value: state.session == null
                    ? '—'
                    : DateTime.fromMillisecondsSinceEpoch(state.session!.pairedAt)
                        .toString()
                        .substring(0, 16),
              ),
              _InfoRow(
                label: 'ADB presence',
                value: device?.state.phonePresent == true ? 'linked' : 'not linked',
              ),
              const SizedBox(height: 4),
              Text(
                device?.state.phonePresent == true
                    ? 'This PC can see the phone over ADB. It can require that link before disarming.'
                    : 'Presence means a direct ADB link (USB or wireless debugging), not relay pairing. '
                        'Only needed if the PC policy requires a trusted phone.',
                style: const TextStyle(fontSize: 10.5, color: C.muted),
              ),
              Align(
                alignment: Alignment.centerRight,
                child: TextButton(
                  onPressed: () => _showPresenceHelp(context),
                  child: const Text('How to link', style: TextStyle(fontSize: 11)),
                ),
              ),
              const SizedBox(height: 8),
              SizedBox(
                width: double.infinity,
                child: OutlinedButton.icon(
                  style: OutlinedButton.styleFrom(foregroundColor: C.danger),
                  onPressed: () async {
                    final ok = await confirmAction(
                      context,
                      title: 'Unpair this PC?',
                      message: 'The phone token is deleted on the server and on this device.',
                      confirmLabel: 'Unpair',
                      danger: true,
                    );
                    if (!ok || !context.mounted) return;
                    await state.unpair();
                  },
                  icon: const Icon(Icons.link_off_rounded, size: 16),
                  label: const Text('Unpair device'),
                ),
              ),
            ],
          ),
        ),
      ],
    );
  }
}

class _SwitchRow extends StatelessWidget {
  final String title;
  final String subtitle;
  final bool value;
  final ValueChanged<bool>? onChanged;

  const _SwitchRow({
    required this.title,
    required this.subtitle,
    required this.value,
    this.onChanged,
  });

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 10),
      child: Row(
        children: [
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(title, style: const TextStyle(fontSize: 13, fontWeight: FontWeight.w600)),
                const SizedBox(height: 3),
                Text(subtitle, style: const TextStyle(fontSize: 11, color: C.muted)),
              ],
            ),
          ),
          Switch(value: value, onChanged: onChanged),
        ],
      ),
    );
  }
}

class _InfoRow extends StatelessWidget {
  final String label;
  final String value;

  const _InfoRow({required this.label, required this.value});

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 6),
      child: Row(
        children: [
          Text(label, style: const TextStyle(fontSize: 12, color: C.muted)),
          const Spacer(),
          Flexible(
            child: Text(
              value,
              textAlign: TextAlign.right,
              overflow: TextOverflow.ellipsis,
              style: const TextStyle(fontSize: 12),
            ),
          ),
        ],
      ),
    );
  }
}
