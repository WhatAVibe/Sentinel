import 'dart:convert';

import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:http/http.dart' as http;
import 'package:mobile_scanner/mobile_scanner.dart';
import 'package:provider/provider.dart';

import '../core/api.dart';
import '../core/app_state.dart';
import '../core/crypto_box.dart';
import '../models.dart';
import '../theme.dart';
import '../widgets.dart';

class PairScreen extends StatefulWidget {
  const PairScreen({super.key});

  @override
  State<PairScreen> createState() => _PairScreenState();
}

class _PairScreenState extends State<PairScreen> {
  final TextEditingController _server = TextEditingController();
  final TextEditingController _code = TextEditingController();
  final TextEditingController _directCode = TextEditingController();

  int _mode = 0;
  bool _busy = false;
  bool _scanLocked = false;
  String? _error;
  String _scanPayload = '';

  bool get _canScan => !kIsWeb && defaultTargetPlatform == TargetPlatform.android;

  @override
  void initState() {
    super.initState();
    if (!_canScan) _mode = 1;
  }

  @override
  void dispose() {
    _server.dispose();
    _code.dispose();
    _directCode.dispose();
    super.dispose();
  }

  Future<void> _finish(Session session) async {
    final state = context.read<AppState>();
    final messenger = ScaffoldMessenger.of(context);
    final navigator = Navigator.of(context);
    await state.attach(session);
    state.unlockApp();
    if (!mounted) return;
    if (navigator.canPop()) navigator.pop();
    messenger.showSnackBar(
      SnackBar(content: Text('Paired with ${session.displayName}')),
    );
  }

  Future<void> _pairRelay({
    String? baseUrl,
    String? code,
    String? requestId,
    String? dataKey,
  }) async {
    final server = baseUrl ?? _server.text;
    final pairCode = (code ?? _code.text).replaceAll(' ', '').toUpperCase();
    if (server.trim().isEmpty) {
      setState(() => _error = 'Enter the server address shown on your PC.');
      return;
    }
    if (pairCode.length != 6) {
      setState(() => _error = 'The pairing code is six characters.');
      return;
    }
    setState(() {
      _busy = true;
      _error = null;
    });
    try {
      final session = await Pairer.pairRelay(
        baseUrl: server,
        pairCode: pairCode,
        dataKey: dataKey ?? '',
        requestId: requestId,
      );
      await _finish(session);
    } catch (error) {
      if (!mounted) return;
      setState(() => _error = describeError(error));
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<void> _pairDirect() async {
    final code = _directCode.text.replaceAll(' ', '').toUpperCase();
    if (code.length != 6) {
      setState(() => _error = 'Enter the six-character code from the PC.');
      return;
    }
    setState(() {
      _busy = true;
      _error = null;
    });
    try {
      final session = await Pairer.pairDirect(
        baseUrl: 'http://127.0.0.1:8790',
        pairCode: code,
      );
      await _finish(session);
    } catch (error) {
      if (!mounted) return;
      setState(() => _error = describeError(error));
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<void> _testRelay() async {
    setState(() {
      _busy = true;
      _error = null;
    });
    try {
      final health = await Pairer.testConnection(_server.text);
      if (!mounted) return;
      setState(() => _error = 'Reached ${health['name'] ?? 'server'} v${health['version'] ?? '?'}');
    } catch (error) {
      if (!mounted) return;
      setState(() => _error = describeError(error));
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<void> _testDirect() async {
    setState(() {
      _busy = true;
      _error = null;
    });
    try {
      final response = await http
          .get(Uri.parse('http://127.0.0.1:8790/local/health'))
          .timeout(const Duration(seconds: 6));
      if (!mounted) return;
      if (response.statusCode == 200) {
        final decoded = jsonDecode(response.body) as Map<String, dynamic>;
        setState(() => _error = 'Direct link ready · ${decoded['device_name'] ?? 'PC'}');
      } else {
        setState(() => _error = 'The PC answered with ${response.statusCode}.');
      }
    } catch (_) {
      if (!mounted) return;
      setState(() => _error = 'No direct link. Enable USB debugging, then run adb reverse on the PC.');
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  void _onDetect(BarcodeCapture capture) {
    if (_scanLocked) return;
    if (capture.barcodes.isEmpty) return;
    final raw = capture.barcodes.first.rawValue;
    if (raw == null || raw.isEmpty || raw == _scanPayload) return;
    _scanPayload = raw;
    try {
      final decoded = jsonDecode(raw);
      if (decoded is! Map<String, dynamic>) throw const FormatException();
      final url = asString(decoded['u']);
      final code = asString(decoded['c']);
      final requestId = asString(decoded['r']);
      final dataKey = asString(decoded['k']).trim();
      if (url.isEmpty || code.isEmpty) throw const FormatException();
      if (!CryptoBox.isValidKey(dataKey)) {
        setState(() {
          _error = 'This QR code is missing the encryption key. Generate a new pairing code on the PC.';
          _scanPayload = '';
        });
        return;
      }
      _scanLocked = true;
      _pairRelay(baseUrl: url, code: code, requestId: requestId, dataKey: dataKey)
          .whenComplete(() {
        if (mounted) {
          _scanLocked = false;
          _scanPayload = '';
        }
      });
    } catch (_) {
      setState(() => _error = 'That QR code is not a Sentinel pairing code.');
    }
  }

  @override
  Widget build(BuildContext context) {
    final modes = _canScan ? ['Scan QR', 'Code', 'Direct'] : ['Code', 'Direct'];

    return Scaffold(
      body: Container(
        decoration: const BoxDecoration(
          gradient: RadialGradient(
            center: Alignment(0, -0.8),
            radius: 1.4,
            colors: [Color(0xFF141A27), C.bg],
          ),
        ),
        child: SafeArea(
          child: Center(
            child: SingleChildScrollView(
              padding: const EdgeInsets.all(22),
              child: ConstrainedBox(
                constraints: const BoxConstraints(maxWidth: 460),
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.stretch,
                  children: [
                    Row(
                      children: [
                        const BrandLogo(size: 46, radius: 14),
                        const SizedBox(width: 12),
                        const Column(
                          crossAxisAlignment: CrossAxisAlignment.start,
                          children: [
                            Text('Sentinel',
                                style: TextStyle(fontSize: 20, fontWeight: FontWeight.w700)),
                            Text('Pair with your PC',
                                style: TextStyle(fontSize: 12, color: C.muted)),
                          ],
                        ),
                      ],
                    ),
                    const SizedBox(height: 22),
                    Row(
                      children: [
                        for (var i = 0; i < modes.length; i++) ...[
                          Expanded(
                            child: GestureDetector(
                              onTap: () => setState(() {
                                _mode = i;
                                _error = null;
                              }),
                              child: Container(
                                padding: const EdgeInsets.symmetric(vertical: 10),
                                decoration: BoxDecoration(
                                  color: _mode == i ? C.accentDim : C.elevated,
                                  borderRadius: BorderRadius.circular(10),
                                  border: Border.all(color: _mode == i ? C.accent : C.border),
                                ),
                                child: Text(
                                  modes[i],
                                  textAlign: TextAlign.center,
                                  style: TextStyle(
                                    fontSize: 12,
                                    fontWeight: FontWeight.w600,
                                    color: _mode == i ? C.text : C.muted,
                                  ),
                                ),
                              ),
                            ),
                          ),
                          if (i < modes.length - 1) const SizedBox(width: 8),
                        ],
                      ],
                    ),
                    const SizedBox(height: 14),
                    CardBox(
                      padding: const EdgeInsets.all(18),
                      child: _canScan ? _buildMode(modes[_mode]) : _buildMode(modes[_mode]),
                    ),
                    if (_error != null) ...[
                      const SizedBox(height: 12),
                      Text(
                        _error!,
                        style: TextStyle(
                          fontSize: 12,
                          color: _error!.startsWith('Reached') ||
                                  _error!.startsWith('Direct link ready') ||
                                  _error!.startsWith('The PC answered')
                              ? C.success
                              : C.warning,
                        ),
                      ),
                    ],
                    const SizedBox(height: 18),
                    const Text(
                      'On the PC open Sentinel → Pair window (or Settings → Pair another phone). Keep the phone and PC on the same network, or reach the relay over Tailscale / ngrok.',
                      textAlign: TextAlign.center,
                      style: TextStyle(fontSize: 11, color: C.muted, height: 1.5),
                    ),
                  ],
                ),
              ),
            ),
          ),
        ),
      ),
    );
  }

  Widget _buildMode(String mode) {
    switch (mode) {
      case 'Scan QR':
        return Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            const Text('Point the camera at the pairing QR code',
                style: TextStyle(fontSize: 13, fontWeight: FontWeight.w600)),
            const SizedBox(height: 4),
            const Text('Scanning happens on-device.',
                style: TextStyle(fontSize: 11, color: C.muted)),
            const SizedBox(height: 14),
            ClipRRect(
              borderRadius: BorderRadius.circular(16),
              child: SizedBox(
                height: 280,
                child: MobileScanner(onDetect: _onDetect),
              ),
            ),
            const SizedBox(height: 14),
            OutlinedButton.icon(
              onPressed: () => setState(() {
                _mode = 1;
                _error = null;
              }),
              icon: const Icon(Icons.keyboard_rounded, size: 18),
              label: const Text('Enter the code instead'),
            ),
          ],
        );
      case 'Code':
        return Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            const Text('Server + code', style: TextStyle(fontSize: 13, fontWeight: FontWeight.w600)),
            const SizedBox(height: 4),
            const Text(
              'Encrypted PCs must pair by QR code — the six-character code cannot carry the key.',
              style: TextStyle(fontSize: 11, color: C.muted, height: 1.4),
            ),
            const SizedBox(height: 12),
            TextField(
              controller: _server,
              keyboardType: TextInputType.url,
              autocorrect: false,
              decoration: const InputDecoration(
                labelText: 'Server address',
                hintText: 'http://192.168.1.20:8765',
              ),
            ),
            const SizedBox(height: 10),
            TextField(
              controller: _code,
              textCapitalization: TextCapitalization.characters,
              maxLength: 7,
              style: const TextStyle(fontSize: 18, letterSpacing: 6, fontWeight: FontWeight.w600),
              textAlign: TextAlign.center,
              decoration: const InputDecoration(counterText: '', hintText: 'ABC123'),
            ),
            const SizedBox(height: 6),
            FilledButton(
              onPressed: _busy ? null : () => _pairRelay(),
              child: _busy
                  ? const SizedBox(width: 16, height: 16, child: CircularProgressIndicator(strokeWidth: 2))
                  : const Text('Pair'),
            ),
            TextButton(
              onPressed: _busy ? null : _testRelay,
              child: const Text('Test connection'),
            ),
          ],
        );
      default:
        return Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Row(
              children: [
                const Icon(Icons.usb_rounded, size: 18, color: C.accent),
                const SizedBox(width: 8),
                const Expanded(
                  child: Text('Direct link over ADB',
                      style: TextStyle(fontSize: 13, fontWeight: FontWeight.w600)),
                ),
                Pill('NO SERVER', color: C.accent.withValues(alpha: 0.9)),
              ],
            ),
            const SizedBox(height: 10),
            const Text(
              'Enable USB debugging (or wireless debugging) on the phone, then pair the PC once. The PC runs adb reverse so this app can reach the agent locally.',
              style: TextStyle(fontSize: 11, color: C.muted, height: 1.5),
            ),
            const SizedBox(height: 14),
            TextField(
              controller: _directCode,
              textCapitalization: TextCapitalization.characters,
              maxLength: 7,
              textAlign: TextAlign.center,
              style: const TextStyle(fontSize: 18, letterSpacing: 6, fontWeight: FontWeight.w600),
              decoration: const InputDecoration(counterText: '', hintText: 'PC code'),
            ),
            const SizedBox(height: 4),
            FilledButton(
              onPressed: _busy ? null : _pairDirect,
              child: _busy
                  ? const SizedBox(width: 16, height: 16, child: CircularProgressIndicator(strokeWidth: 2))
                  : const Text('Pair direct'),
            ),
            TextButton(
              onPressed: _busy ? null : _testDirect,
              child: const Text('Test local link'),
            ),
          ],
        );
    }
  }
}
