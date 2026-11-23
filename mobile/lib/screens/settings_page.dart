import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:path_provider/path_provider.dart';
import 'package:provider/provider.dart';
import 'package:share_plus/share_plus.dart';

import '../core/api.dart';
import '../core/app_state.dart';
import '../core/diagnostics.dart';
import '../core/flipper_bridge.dart';
import '../models.dart';
import '../theme.dart';
import '../widgets.dart';
import '../core/widgets_bridge.dart';
import 'flipper_page.dart';
import 'tutorial.dart';

class SettingsPage extends StatelessWidget {
  const SettingsPage({super.key});

  Future<void> _pinWidget(BuildContext context, String kind) async {
    final ok = await SentinelWidgets.pin(kind);
    if (!context.mounted) return;
    ScaffoldMessenger.of(context).showSnackBar(SnackBar(
      content: Text(ok
          ? 'Widget requested. Confirm it on your home screen.'
          : 'Add it from your launcher: long-press the home screen → Widgets → Sentinel.'),
    ));
  }

  Future<void> _export(BuildContext context, String what, String format) async {
    final state = context.read<AppState>();
    final messenger = ScaffoldMessenger.of(context);
    try {
      final bytes = await state.exportHistory(what, format);
      final dir = await getTemporaryDirectory();
      final stamp = DateTime.now().toIso8601String().replaceAll(':', '-').substring(0, 19);
      final file = File('${dir.path}/sentinel-$what-$stamp.$format');
      await file.writeAsBytes(bytes, flush: true);
      await Share.shareXFiles(
        [XFile(file.path, mimeType: format == 'csv' ? 'text/csv' : 'application/json')],
        subject: 'Sentinel $what export',
      );
    } catch (error) {
      messenger.showSnackBar(SnackBar(content: Text(describeError(error))));
    }
  }

  Future<void> _editServer(BuildContext context) async {
    final state = context.read<AppState>();
    final session = state.session;
    if (session == null) return;
    final controller = TextEditingController(text: session.baseUrl);
    final url = await showDialog<String>(
      context: context,
      builder: (context) => AlertDialog(
        backgroundColor: C.surface,
        shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(18)),
        title: const Text('Server address', style: TextStyle(fontSize: 16)),
        content: TextField(
          controller: controller,
          keyboardType: TextInputType.url,
          autocorrect: false,
          decoration: const InputDecoration(hintText: 'http://192.168.1.20:8765'),
        ),
        actions: [
          TextButton(onPressed: () => Navigator.pop(context), child: const Text('Cancel')),
          FilledButton(
            onPressed: () => Navigator.pop(context, controller.text.trim()),
            child: const Text('Save'),
          ),
        ],
      ),
    );
    if (url == null || url.isEmpty || !context.mounted) return;
    final base = normalizeBaseUrl(url);
    final updated = session.withBaseUrl(base);
    await state.attach(updated);
    if (!context.mounted) return;
    ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text('Server set to $base')));
  }

  Future<void> _changePin(BuildContext context) async {
    final state = context.read<AppState>();
    final first = TextEditingController();
    final second = TextEditingController();
    final ok = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        backgroundColor: C.surface,
        shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(18)),
        title: const Text('Quick-unlock PIN', style: TextStyle(fontSize: 16)),
        content: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            TextField(
              controller: first,
              obscureText: true,
              keyboardType: TextInputType.number,
              maxLength: 4,
              textAlign: TextAlign.center,
              decoration: const InputDecoration(counterText: '', hintText: 'new PIN'),
            ),
            const SizedBox(height: 8),
            TextField(
              controller: second,
              obscureText: true,
              keyboardType: TextInputType.number,
              maxLength: 4,
              textAlign: TextAlign.center,
              decoration: const InputDecoration(counterText: '', hintText: 'confirm PIN'),
            ),
          ],
        ),
        actions: [
          TextButton(onPressed: () => Navigator.pop(context, false), child: const Text('Cancel')),
          FilledButton(
            onPressed: () => Navigator.pop(context, true),
            child: const Text('Save'),
          ),
        ],
      ),
    );
    if (ok != true || !context.mounted) return;
    final pin = first.text.trim();
    if (pin.length != 4 || pin != second.text.trim()) {
      ScaffoldMessenger.of(context).showSnackBar(
        const SnackBar(content: Text('PIN must be 4 digits and match.')),
      );
      return;
    }
    await state.savePin(pin);
    if (!context.mounted) return;
    ScaffoldMessenger.of(context).showSnackBar(const SnackBar(content: Text('PIN updated')));
  }

  void _copyDiagnostics(BuildContext context) {
    final entries = DiagLog.instance.snapshot(limit: 200);
    if (entries.isEmpty) return;
    final text = entries
        .map((e) => '${e.timeText} [${e.tag}] ${e.message}')
        .join('\n');
    Clipboard.setData(ClipboardData(text: text));
    ScaffoldMessenger.of(context).showSnackBar(
      const SnackBar(content: Text('Diagnostics copied')),
    );
  }

  @override
  Widget build(BuildContext context) {
    final state = context.watch<AppState>();
    final flipper = context.watch<FlipperBridge>();
    final session = state.session;

    return Scaffold(
      appBar: AppBar(
        title: const Text('Settings'),
        leading: IconButton(
          icon: const Icon(Icons.arrow_back_rounded, size: 20),
          onPressed: () => Navigator.of(context).pop(),
        ),
      ),
      body: ListView(
        padding: const EdgeInsets.fromLTRB(16, 6, 16, 28),
        children: [
          const SectionHeader('Connection'),
          CardBox(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Row(
                  children: [
                    const Icon(Icons.dns_rounded, size: 18, color: C.accent),
                    const SizedBox(width: 8),
                    Expanded(
                      child: Text(
                        session?.label ?? 'Not paired',
                        style: const TextStyle(fontSize: 14, fontWeight: FontWeight.w600),
                      ),
                    ),
                    Pill(session?.isDirect == true ? 'DIRECT' : 'RELAY',
                        color: session?.isDirect == true ? C.accent : C.success),
                  ],
                ),
                const SizedBox(height: 8),
                Text(
                  session?.baseUrl ?? '—',
                  style: const TextStyle(fontSize: 11, color: C.muted),
                ),
                const SizedBox(height: 4),
                Text(
                  state.relayOnline
                      ? (state.device?.online == true
                          ? 'Relay reachable · ${state.device?.name ?? 'PC'} online'
                          : 'Relay reachable · ${state.device?.name ?? 'PC'} offline')
                      : 'Relay unreachable',
                  style: TextStyle(
                    fontSize: 11,
                    color: state.relayOnline
                        ? (state.device?.online == true ? C.success : C.muted)
                        : C.warning,
                  ),
                ),
                const SizedBox(height: 14),
                Row(
                  children: [
                    Expanded(
                      child: OutlinedButton.icon(
                        onPressed: () async {
                          await state.refreshDevice();
                          if (!context.mounted) return;
                          ScaffoldMessenger.of(context).showSnackBar(
                            SnackBar(
                              content: Text(state.relayOnline
                                  ? (state.device?.online == true
                                      ? 'Relay OK · connected to ${state.device?.name ?? 'PC'}'
                                      : 'Relay OK · ${state.device?.name ?? 'PC'} is offline')
                                  : (state.lastError ?? 'No response')),
                            ),
                          );
                        },
                        icon: const Icon(Icons.wifi_tethering_rounded, size: 16),
                        label: const Text('Test'),
                      ),
                    ),
                    const SizedBox(width: 10),
                    Expanded(
                      child: OutlinedButton.icon(
                        onPressed: session?.isDirect == true ? null : () => _editServer(context),
                        icon: const Icon(Icons.edit_rounded, size: 16),
                        label: const Text('Address'),
                      ),
                    ),
                  ],
                ),
              ],
            ),
          ),
          const SectionHeader('Encryption'),
          CardBox(
            padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 6),
            child: Column(
              children: [
                Row(
                  children: [
                    Icon(
                      session?.encrypted == true ? Icons.lock_rounded : Icons.lock_open_rounded,
                      size: 18,
                      color: session?.encrypted == true ? C.success : C.warning,
                    ),
                    const SizedBox(width: 10),
                    Expanded(
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          Text(
                            session?.encrypted == true
                                ? 'End-to-end encryption is on'
                                : (session?.isDirect == true
                                    ? 'Plaintext over the direct link'
                                    : 'No encryption key on this session'),
                            style: const TextStyle(fontSize: 13, fontWeight: FontWeight.w600),
                          ),
                          const SizedBox(height: 2),
                          Text(
                            session?.encrypted == true
                                ? 'Events, messages and screenshots are decrypted on this phone. The relay only stores ciphertext.'
                                : (session?.isDirect == true
                                    ? 'Direct ADB mode never goes through the relay. Pair over the relay to enable encryption.'
                                    : "Scan the PC's QR code again to receive the data key."),
                            style: const TextStyle(fontSize: 10.5, color: C.muted, height: 1.4),
                          ),
                        ],
                      ),
                    ),
                  ],
                ),
                const Divider(color: C.border, height: 1),
                const _WatchPairTile(),
              ],
            ),
          ),
          const SectionHeader('Flipper'),
          CardBox(
            child: Row(
              children: [
                Icon(
                  Icons.watch_rounded,
                  size: 20,
                  color: flipper.connected ? C.success : C.muted,
                ),
                const SizedBox(width: 10),
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        flipper.connected
                            ? 'Linked to this phone'
                            : (flipper.connecting || flipper.reconnecting
                                ? 'Reconnecting…'
                                : 'Bluetooth bridge'),
                        style: const TextStyle(fontSize: 13, fontWeight: FontWeight.w600),
                      ),
                      const SizedBox(height: 2),
                      Text(
                        flipper.connected || flipper.connecting || flipper.reconnecting
                            ? (flipper.statusText ?? 'Flipper connected')
                            : 'Control Sentinel from the Flipper without a WiFi board',
                        style: const TextStyle(fontSize: 10.5, color: C.muted),
                      ),
                    ],
                  ),
                ),
                TextButton(
                  onPressed: () => Navigator.of(context).push(
                    MaterialPageRoute(builder: (_) => const FlipperPage()),
                  ),
                  child: Text(flipper.connected ? 'Manage' : 'Set up',
                      style: const TextStyle(fontSize: 11)),
                ),
              ],
            ),
          ),
          const SectionHeader('Linked phones'),
          const PhonesCard(),
          const SectionHeader('App lock'),
          CardBox(
            padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 6),
            child: Column(
              children: [
                Row(
                  children: [
                    Expanded(
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          const Text('Biometric unlock',
                              style: TextStyle(fontSize: 13, fontWeight: FontWeight.w600)),
                          const SizedBox(height: 3),
                          Text(
                            state.canUseBiometrics
                                ? 'Ask for fingerprint or face on launch'
                                : 'Not available here — the PIN is used',
                            style: const TextStyle(fontSize: 11, color: C.muted),
                          ),
                        ],
                      ),
                    ),
                    Switch(
                      value: state.biometricEnabled && state.canUseBiometrics,
                      onChanged: state.canUseBiometrics
                          ? (value) => state.setBiometricEnabled(value)
                          : null,
                    ),
                  ],
                ),
                const Divider(color: C.border, height: 1),
                ListTile(
                  contentPadding: EdgeInsets.zero,
                  leading: const Icon(Icons.pin_rounded, size: 18),
                  title: const Text('Change quick-unlock PIN',
                      style: TextStyle(fontSize: 13, fontWeight: FontWeight.w600)),
                  subtitle: const Text('Local only, separate from the 2FA code',
                      style: TextStyle(fontSize: 11, color: C.muted)),
                  trailing: const Icon(Icons.chevron_right_rounded, size: 18, color: C.muted),
                  onTap: () => _changePin(context),
                ),
                const Divider(color: C.border, height: 1),
                ListTile(
                  contentPadding: EdgeInsets.zero,
                  leading: const Icon(Icons.school_outlined, size: 18),
                  title: const Text('Show tutorial',
                      style: TextStyle(fontSize: 13, fontWeight: FontWeight.w600)),
                  subtitle: const Text('Walk through the app again',
                      style: TextStyle(fontSize: 11, color: C.muted)),
                  trailing: const Icon(Icons.chevron_right_rounded, size: 18, color: C.muted),
                  onTap: () => showSentinelTutorial(context, force: true),
                ),
                const Divider(color: C.border, height: 1),
                ListTile(
                  contentPadding: EdgeInsets.zero,
                  leading: const Icon(Icons.lock_clock_rounded, size: 18),
                  title: const Text('Lock now',
                      style: TextStyle(fontSize: 13, fontWeight: FontWeight.w600)),
                  trailing: const Icon(Icons.chevron_right_rounded, size: 18, color: C.muted),
                  onTap: state.lockApp,
                ),
              ],
            ),
          ),
          const SectionHeader('Notifications'),
          CardBox(
            padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 4),
            child: Column(
              children: [
                _NotifyRow(state: state, category: 'security', title: 'Security events',
                    subtitle: 'Locks, away mode, unlocks, pairing'),
                const Divider(color: C.border, height: 1),
                _NotifyRow(state: state, category: 'power', title: 'Power events',
                    subtitle: 'Offline, back online, restarts'),
                const Divider(color: C.border, height: 1),
                _NotifyRow(state: state, category: 'message', title: 'Messages and replies',
                    subtitle: 'Messages sent to the PC and replies from it'),
                const Divider(color: C.border, height: 1),
                _NotifyRow(state: state, category: 'screenshot', title: 'Screenshots',
                    subtitle: 'A capture was uploaded to the relay'),
                const Divider(color: C.border, height: 1),
                _NotifyRow(state: state, category: 'clipboard', title: 'Clipboard events',
                    subtitle: 'Clipboard activity while monitoring'),
                const Divider(color: C.border, height: 1),
                _NotifyRow(state: state, category: 'other', title: 'Everything else',
                    subtitle: 'Windows, apps, USB and other activity'),
              ],
            ),
          ),
          const SectionHeader('Widgets & Quick Settings'),
          CardBox(
            padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 4),
            child: Column(
              children: [
                SwitchListTile(
                  contentPadding: EdgeInsets.zero,
                  value: state.widgetAllowWithoutUnlock,
                  onChanged: state.setWidgetAllowWithoutUnlock,
                  title: const Text('Allow actions without unlocking',
                      style: TextStyle(fontSize: 13, fontWeight: FontWeight.w600)),
                  subtitle: const Text(
                      'Lock, secure lock, away and screenshots straight from a widget or tile. '
                      'Off: those buttons open the app and its PIN/biometrics.',
                      style: TextStyle(fontSize: 11, color: C.muted)),
                ),
                const Divider(color: C.border, height: 1),
                SwitchListTile(
                  contentPadding: EdgeInsets.zero,
                  value: state.widgetShowCode,
                  onChanged: state.setWidgetShowCode,
                  title: const Text('Show the 2FA code in widgets',
                      style: TextStyle(fontSize: 13, fontWeight: FontWeight.w600)),
                  subtitle: const Text('The code is encrypted at rest with Android Keystore',
                      style: TextStyle(fontSize: 11, color: C.muted)),
                ),
                const Divider(color: C.border, height: 1),
                ListTile(
                  contentPadding: EdgeInsets.zero,
                  title: const Text('Add status widget', style: TextStyle(fontSize: 13)),
                  subtitle: const Text('Name, state, code and countdown',
                      style: TextStyle(fontSize: 11, color: C.muted)),
                  trailing: const Icon(Icons.add_rounded, size: 18, color: C.accent),
                  onTap: () => _pinWidget(context, 'status'),
                ),
                const Divider(color: C.border, height: 1),
                ListTile(
                  contentPadding: EdgeInsets.zero,
                  title: const Text('Add quick actions widget', style: TextStyle(fontSize: 13)),
                  subtitle: const Text('Lock, secure lock, away and screenshot buttons',
                      style: TextStyle(fontSize: 11, color: C.muted)),
                  trailing: const Icon(Icons.add_rounded, size: 18, color: C.accent),
                  onTap: () => _pinWidget(context, 'actions'),
                ),
                const Divider(color: C.border, height: 1),
                ListTile(
                  contentPadding: EdgeInsets.zero,
                  title: const Text('Add code-only widget', style: TextStyle(fontSize: 13)),
                  subtitle: const Text('A compact live TOTP code',
                      style: TextStyle(fontSize: 11, color: C.muted)),
                  trailing: const Icon(Icons.add_rounded, size: 18, color: C.accent),
                  onTap: () => _pinWidget(context, 'code'),
                ),
              ],
            ),
          ),
          const SectionHeader('Export'),
          CardBox(
            padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 4),
            child: Column(
              children: [
                ListTile(
                  contentPadding: EdgeInsets.zero,
                  title: const Text('Activity log (JSON)', style: TextStyle(fontSize: 13)),
                  subtitle: const Text('Everything the relay still retains for this PC',
                      style: TextStyle(fontSize: 11, color: C.muted)),
                  trailing: const Icon(Icons.ios_share_rounded, size: 18, color: C.muted),
                  onTap: () => _export(context, 'events', 'json'),
                ),
                const Divider(color: C.border, height: 1),
                ListTile(
                  contentPadding: EdgeInsets.zero,
                  title: const Text('Activity log (CSV)', style: TextStyle(fontSize: 13)),
                  subtitle: const Text('Spreadsheet-friendly, UTC timestamps',
                      style: TextStyle(fontSize: 11, color: C.muted)),
                  trailing: const Icon(Icons.ios_share_rounded, size: 18, color: C.muted),
                  onTap: () => _export(context, 'events', 'csv'),
                ),
                const Divider(color: C.border, height: 1),
                ListTile(
                  contentPadding: EdgeInsets.zero,
                  title: const Text('Audit log (CSV)', style: TextStyle(fontSize: 13)),
                  subtitle: const Text('Pairing, commands, unlocks and bad-code attempts',
                      style: TextStyle(fontSize: 11, color: C.muted)),
                  trailing: const Icon(Icons.ios_share_rounded, size: 18, color: C.muted),
                  onTap: () => _export(context, 'audit', 'csv'),
                ),
              ],
            ),
          ),
          const SectionHeader('About'),
          CardBox(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                const _Line('App', 'Sentinel 2.0.0'),
                _Line('Relay', state.relayVersion.isEmpty ? 'checking…' : state.relayVersion),
                _Line('Agent', state.device?.agentVersion.isEmpty ?? true
                    ? 'unknown'
                    : state.device!.agentVersion),
                const _Line('Storage', 'Session token in secure storage'),
                const SizedBox(height: 6),
                const Text(
                  'Sentinel only controls your own PC. No account, no analytics, no third-party services.',
                  style: TextStyle(fontSize: 11, color: C.muted, height: 1.5),
                ),
              ],
            ),
          ),
          const SectionHeader('Diagnostics'),
          CardBox(
            padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 10),
            child: Column(
              children: [
                Row(
                  children: [
                    const Icon(Icons.terminal_rounded, size: 18, color: C.accent),
                    const SizedBox(width: 8),
                    const Text('Live log',
                        style: TextStyle(fontSize: 13, fontWeight: FontWeight.w600)),
                    const Spacer(),
                    TextButton(
                      onPressed: () => _copyDiagnostics(context),
                      child: const Text('Copy'),
                    ),
                    TextButton(
                      onPressed: () => DiagLog.instance.clear(),
                      child: const Text('Clear'),
                    ),
                  ],
                ),
                AnimatedBuilder(
                  animation: DiagLog.instance.revision,
                  builder: (context, _) {
                    final entries = DiagLog.instance.snapshot(limit: 40);
                    if (entries.isEmpty) {
                      return const Padding(
                        padding: EdgeInsets.fromLTRB(2, 6, 2, 14),
                        child: Align(
                          alignment: Alignment.centerLeft,
                          child: Text('No traffic yet.',
                              style: TextStyle(fontSize: 11, color: C.muted)),
                        ),
                      );
                    }
                    return Column(
                      children: [for (final entry in entries) _DiagRow(entry)],
                    );
                  },
                ),
                const SizedBox(height: 4),
              ],
            ),
          ),
          const SectionHeader('Danger zone'),
          CardBox(
            child: Column(
              children: [
                SizedBox(
                  width: double.infinity,
                  child: OutlinedButton.icon(
                    style: OutlinedButton.styleFrom(foregroundColor: C.warning),
                    onPressed: () async {
                      final code = await showCodeSheet(
                        context,
                        title: 'Authorize PC agent exit',
                        actionLabel: 'Quit PC agent',
                      );
                      if (code == null || !context.mounted) return;
                      final result = await state.command('agent_exit', code: code);
                      if (!context.mounted) return;
                      ScaffoldMessenger.of(context).showSnackBar(
                        SnackBar(
                          content: Text(result.ok
                              ? 'Agent exit authorized — the watchdog stops too'
                              : friendlyError(result.error)),
                        ),
                      );
                    },
                    icon: const Icon(Icons.stop_circle_outlined, size: 16),
                    label: const Text('Quit PC agent (needs code)'),
                  ),
                ),
                const SizedBox(height: 10),
                SizedBox(
                  width: double.infinity,
                  child: OutlinedButton.icon(
                    style: OutlinedButton.styleFrom(foregroundColor: C.danger),
                    onPressed: () async {
                      final ok = await confirmAction(
                        context,
                        title: 'Unpair this PC?',
                        message: 'Deletes the phone token on the server and on this device.',
                        confirmLabel: 'Unpair',
                        danger: true,
                      );
                      if (!ok || !context.mounted) return;
                      await state.unpair();
                      if (!context.mounted) return;
                      Navigator.of(context).popUntil((route) => route.isFirst);
                    },
                    icon: const Icon(Icons.link_off_rounded, size: 16),
                    label: const Text('Unpair device'),
                  ),
                ),
                const SizedBox(height: 10),
                SizedBox(
                  width: double.infinity,
                  child: OutlinedButton.icon(
                    onPressed: () async {
                      final ok = await confirmAction(
                        context,
                        title: 'Sign out of this phone?',
                        message: 'The PC keeps its pairing. You will need a new code to reconnect this phone.',
                        confirmLabel: 'Sign out',
                        danger: true,
                      );
                      if (!ok || !context.mounted) return;
                      await state.signOut();
                      if (!context.mounted) return;
                      Navigator.of(context).popUntil((route) => route.isFirst);
                    },
                    icon: const Icon(Icons.logout_rounded, size: 16),
                    label: const Text('Sign out'),
                  ),
                ),
              ],
            ),
          ),
        ],
      ),
    );
  }
}

class _WatchPairTile extends StatefulWidget {
  const _WatchPairTile();

  @override
  State<_WatchPairTile> createState() => _WatchPairTileState();
}

class _WatchPairTileState extends State<_WatchPairTile> {
  bool _busy = false;

  Future<void> _pair() async {
    if (_busy) return;
    setState(() => _busy = true);
    final result = await context.read<AppState>().pairWatch();
    if (!mounted) return;
    setState(() => _busy = false);
    ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(result.message)));
  }

  @override
  Widget build(BuildContext context) {
    final state = context.watch<AppState>();
    final direct = state.session?.isDirect ?? false;
    final encrypted = state.session?.encrypted ?? false;
    return ListTile(
      contentPadding: EdgeInsets.zero,
      enabled: !_busy,
      leading: const Icon(Icons.watch_rounded, size: 18),
      title: const Text('Pair a Wear OS watch',
          style: TextStyle(fontSize: 13, fontWeight: FontWeight.w600)),
      subtitle: Text(
        direct
            ? 'Watches pair through the relay. Switch to a relay session first.'
            : (encrypted
                ? 'Adds the watch over Bluetooth and sends it the key. The relay never sees it.'
                : "Requires the encryption key from the PC's QR code."),
        style: const TextStyle(fontSize: 11, color: C.muted, height: 1.4),
      ),
      trailing: _busy
          ? const SizedBox(width: 16, height: 16, child: CircularProgressIndicator(strokeWidth: 2))
          : const Icon(Icons.chevron_right_rounded, size: 18, color: C.muted),
      onTap: direct || _busy ? null : _pair,
    );
  }
}

class _NotifyRow extends StatelessWidget {
  final AppState state;
  final String category;
  final String title;
  final String subtitle;

  const _NotifyRow({
    required this.state,
    required this.category,
    required this.title,
    required this.subtitle,
  });

  @override
  Widget build(BuildContext context) {
    return SwitchListTile(
      contentPadding: EdgeInsets.zero,
      value: state.notifications[category] ?? true,
      onChanged: (value) => state.setNotificationEnabled(category, value),
      title: Text(title, style: const TextStyle(fontSize: 13, fontWeight: FontWeight.w600)),
      subtitle: Text(subtitle, style: const TextStyle(fontSize: 11, color: C.muted)),
    );
  }
}

class _Line extends StatelessWidget {  final String label;
  final String value;

  const _Line(this.label, this.value);
  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 5),
      child: Row(
        children: [
          Text(label, style: const TextStyle(fontSize: 12, color: C.muted)),
          const Spacer(),
          Text(value, style: const TextStyle(fontSize: 12)),
        ],
      ),
    );
  }
}

class _DiagRow extends StatelessWidget {  final DiagEntry entry;

  const _DiagRow(this.entry);

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 4),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            entry.timeText,
            style: const TextStyle(fontSize: 10, color: C.muted, fontFamily: 'monospace'),
          ),
          const SizedBox(width: 8),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  entry.tag.toUpperCase(),
                  style: const TextStyle(fontSize: 8, color: C.muted, letterSpacing: 0.5),
                ),
                Text(
                  entry.message,
                  maxLines: 3,
                  overflow: TextOverflow.ellipsis,
                  style: TextStyle(
                    fontSize: 10.5,
                    height: 1.35,
                    color: entry.error ? C.danger : C.text,
                  ),
                ),
              ],
            ),
          ),
        ],
      ),
    );
  }
}

class PhonesCard extends StatefulWidget {
  const PhonesCard({super.key});

  @override
  State<PhonesCard> createState() => _PhonesCardState();
}

class _PhonesCardState extends State<PhonesCard> {
  List<PhoneInfo> _phones = const [];
  bool _loading = true;
  String? _error;

  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _load() async {
    final state = context.read<AppState>();
    final client = state.client;
    if (client == null) {
      setState(() {
        _loading = false;
        _phones = const [];
      });
      return;
    }
    setState(() {
      _loading = true;
      _error = null;
    });
    try {
      final phones = await client.fetchPhones();
      if (!mounted) return;
      setState(() {
        _phones = phones;
        _loading = false;
      });
    } catch (error) {
      if (!mounted) return;
      setState(() {
        _error = describeError(error);
        _loading = false;
      });
    }
  }

  Future<void> _remove(PhoneInfo phone) async {
    final ok = await confirmAction(
      context,
      title: 'Remove ${phone.name}?',
      message: 'That phone loses access immediately. You can pair it again later.',
      confirmLabel: 'Remove',
      danger: true,
    );
    if (!ok || !mounted) return;
    try {
      await context.read<AppState>().client?.removePhone(phone.id);
      await _load();
    } catch (error) {
      if (!mounted) return;
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(content: Text(describeError(error))),
      );
    }
  }

  String _date(int? milliseconds) {
    if (milliseconds == null) return 'never';
    final time = DateTime.fromMillisecondsSinceEpoch(milliseconds);
    return time.toString().substring(0, 16);
  }

  @override
  Widget build(BuildContext context) {
    final state = context.watch<AppState>();
    final direct = state.session?.isDirect ?? false;
    return CardBox(
      padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 12),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              const Icon(Icons.devices_rounded, size: 18, color: C.accent),
              const SizedBox(width: 8),
              const Text('Phones with access',
                  style: TextStyle(fontSize: 13, fontWeight: FontWeight.w600)),
              const Spacer(),
              IconButton(
                onPressed: _loading ? null : _load,
                icon: const Icon(Icons.refresh_rounded, size: 18, color: C.muted),
              ),
            ],
          ),
          if (direct)
            const Padding(
              padding: EdgeInsets.symmetric(vertical: 8),
              child: Text(
                'Direct link mode has no server-side phone list. Pair through the relay to manage phones.',
                style: TextStyle(fontSize: 11, color: C.muted, height: 1.4),
              ),
            )
          else if (_loading)
            const Padding(
              padding: EdgeInsets.symmetric(vertical: 14),
              child: Center(child: SizedBox(width: 18, height: 18, child: CircularProgressIndicator(strokeWidth: 2))),
            )
          else if (_error != null)
            Padding(
              padding: const EdgeInsets.symmetric(vertical: 10),
              child: Text(_error!, style: const TextStyle(fontSize: 11, color: C.warning)),
            )
          else if (_phones.isEmpty)
            const Padding(
              padding: EdgeInsets.symmetric(vertical: 10),
              child: Text('No phones linked.', style: TextStyle(fontSize: 11, color: C.muted)),
            )
          else
            for (final phone in _phones)
              Padding(
                padding: const EdgeInsets.symmetric(vertical: 8),
                child: Row(
                  children: [
                    Expanded(
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          Row(
                            children: [
                              Text(phone.name,
                                  style: const TextStyle(fontSize: 13, fontWeight: FontWeight.w600)),
                              if (phone.current) ...[
                                const SizedBox(width: 8),
                                const Pill('THIS PHONE', color: C.success),
                              ],
                            ],
                          ),
                          const SizedBox(height: 3),
                          Text(
                            'linked ${_date(phone.createdAt)} · last seen ${_date(phone.lastSeen)}',
                            style: const TextStyle(fontSize: 10.5, color: C.muted),
                          ),
                        ],
                      ),
                    ),
                    if (!phone.current)
                      TextButton(
                        onPressed: () => _remove(phone),
                        style: TextButton.styleFrom(foregroundColor: C.danger),
                        child: const Text('Remove'),
                      ),
                  ],
                ),
              ),
        ],
      ),
    );
  }
}

