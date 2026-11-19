import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:provider/provider.dart';

import '../core/api.dart';
import '../core/app_state.dart';
import '../core/diagnostics.dart';
import '../core/flipper_bridge.dart';
import '../models.dart';
import '../theme.dart';
import '../widgets.dart';
import 'flipper_page.dart';

class SettingsPage extends StatelessWidget {
  const SettingsPage({super.key});

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
    final updated = Session(
      baseUrl: base,
      mode: session.mode,
      token: session.token,
      totpSecret: session.totpSecret,
      deviceId: session.deviceId,
      deviceName: session.deviceName,
      label: session.label,
      pairedAt: session.pairedAt,
    );
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
                  leading: const Icon(Icons.lock_clock_rounded, size: 18),
                  title: const Text('Lock now',
                      style: TextStyle(fontSize: 13, fontWeight: FontWeight.w600)),
                  trailing: const Icon(Icons.chevron_right_rounded, size: 18, color: C.muted),
                  onTap: state.lockApp,
                ),
              ],
            ),
          ),
          const SectionHeader('About'),
          CardBox(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                const _Line('App', 'Sentinel 1.2.5'),
                const _Line('Relay protocol', 'v1.1'),
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

class _Line extends StatelessWidget {
  final String label;
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

