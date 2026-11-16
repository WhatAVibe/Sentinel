import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../core/app_state.dart';
import '../core/biometrics.dart';
import '../theme.dart';
import '../widgets.dart';

class LockScreen extends StatefulWidget {
  const LockScreen({super.key});

  @override
  State<LockScreen> createState() => _LockScreenState();
}

class _LockScreenState extends State<LockScreen> {
  final TextEditingController _pin = TextEditingController();
  final TextEditingController _confirm = TextEditingController();
  String? _error;
  bool _busy = false;
  bool _prompted = false;

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addPostFrameCallback((_) => _maybePromptBiometrics());
  }

  @override
  void dispose() {
    _pin.dispose();
    _confirm.dispose();
    super.dispose();
  }

  Future<void> _maybePromptBiometrics() async {
    final state = context.read<AppState>();
    if (_prompted || !state.biometricEnabled || !state.canUseBiometrics) return;
    _prompted = true;
    final ok = await Biometrics.authenticate(reason: 'Unlock Sentinel');
    if (!mounted) return;
    if (ok) state.unlockApp();
  }

  Future<void> _biometric() async {
    final state = context.read<AppState>();
    if (!state.canUseBiometrics) {
      setState(() => _error = 'No biometric hardware available. Use your PIN.');
      return;
    }
    setState(() => _busy = true);
    final ok = await Biometrics.authenticate(reason: 'Unlock Sentinel');
    if (!mounted) return;
    setState(() => _busy = false);
    if (ok) state.unlockApp();
  }

  Future<void> _unlockWithPin() async {
    final state = context.read<AppState>();
    setState(() {
      _busy = true;
      _error = null;
    });
    final ok = await state.verifyPin(_pin.text.trim());
    if (!mounted) return;
    setState(() => _busy = false);
    if (ok) {
      _pin.clear();
      state.unlockApp();
    } else {
      setState(() => _error = 'Wrong PIN.');
    }
  }

  Future<void> _savePin() async {
    final pin = _pin.text.trim();
    if (pin.length != 4) {
      setState(() => _error = 'Choose a 4-digit PIN.');
      return;
    }
    if (pin != _confirm.text.trim()) {
      setState(() => _error = 'PINs do not match.');
      return;
    }
    final state = context.read<AppState>();
    await state.savePin(pin);
    if (!mounted) return;
    _pin.clear();
    _confirm.clear();
    state.unlockApp();
  }

  @override
  Widget build(BuildContext context) {
    final state = context.watch<AppState>();
    final setup = !state.hasPin;

    return Scaffold(
      body: Container(
        decoration: const BoxDecoration(
          gradient: RadialGradient(
            center: Alignment(0, -0.7),
            radius: 1.3,
            colors: [Color(0xFF141A27), C.bg],
          ),
        ),
        child: SafeArea(
          child: Center(
            child: SingleChildScrollView(
              padding: const EdgeInsets.all(24),
              child: ConstrainedBox(
                constraints: const BoxConstraints(maxWidth: 420),
                child: Column(
                  mainAxisSize: MainAxisSize.min,
                  crossAxisAlignment: CrossAxisAlignment.stretch,
                  children: [
                    Center(
                      child: const BrandLogo(size: 62, radius: 18),
                    ),
                    const SizedBox(height: 18),
                    const Text(
                      'Sentinel',
                      textAlign: TextAlign.center,
                      style: TextStyle(fontSize: 24, fontWeight: FontWeight.w700, letterSpacing: -0.5),
                    ),
                    const SizedBox(height: 4),
                    Text(
                      state.session?.deviceName ?? 'Remote control for your PC',
                      textAlign: TextAlign.center,
                      style: const TextStyle(fontSize: 12, color: C.muted),
                    ),
                    const SizedBox(height: 26),
                    CardBox(
                      padding: const EdgeInsets.all(20),
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.stretch,
                        children: [
                          Text(
                            setup ? 'Create a quick-unlock PIN' : 'Locked',
                            style: const TextStyle(fontSize: 15, fontWeight: FontWeight.w600),
                          ),
                          const SizedBox(height: 4),
                          Text(
                            setup
                                ? 'This PIN only unlocks this app. Your 2FA code stays separate.'
                                : 'Confirm it is you to reconnect to your PC.',
                            style: const TextStyle(fontSize: 12, color: C.muted, height: 1.4),
                          ),
                          const SizedBox(height: 16),
                          TextField(
                            controller: _pin,
                            obscureText: true,
                            keyboardType: TextInputType.number,
                            maxLength: 4,
                            textAlign: TextAlign.center,
                            style: const TextStyle(fontSize: 24, letterSpacing: 14, fontWeight: FontWeight.w600),
                            decoration: const InputDecoration(counterText: '', hintText: '••••'),
                            onSubmitted: (_) => setup ? _savePin() : _unlockWithPin(),
                          ),
                          if (setup) ...[
                            const SizedBox(height: 10),
                            TextField(
                              controller: _confirm,
                              obscureText: true,
                              keyboardType: TextInputType.number,
                              maxLength: 4,
                              textAlign: TextAlign.center,
                              style: const TextStyle(fontSize: 24, letterSpacing: 14, fontWeight: FontWeight.w600),
                              decoration: const InputDecoration(counterText: '', hintText: 'confirm'),
                            ),
                          ],
                          if (_error != null) ...[
                            const SizedBox(height: 8),
                            Text(_error!, style: const TextStyle(color: C.danger, fontSize: 12)),
                          ],
                          const SizedBox(height: 14),
                          FilledButton(
                            onPressed: _busy ? null : (setup ? _savePin : _unlockWithPin),
                            child: _busy
                                ? const SizedBox(
                                    width: 16, height: 16, child: CircularProgressIndicator(strokeWidth: 2))
                                : Text(setup ? 'Set PIN & continue' : 'Unlock'),
                          ),
                          if (!setup && state.biometricEnabled) ...[
                            const SizedBox(height: 10),
                            OutlinedButton.icon(
                              onPressed: _busy ? null : _biometric,
                              icon: const Icon(Icons.fingerprint_rounded, size: 18),
                              label: const Text('Use biometrics'),
                            ),
                          ],
                        ],
                      ),
                    ),
                    const SizedBox(height: 16),
                    TextButton(
                      onPressed: () async {
                        final ok = await confirmAction(
                          context,
                          title: 'Disconnect this phone?',
                          message: 'You will need the pairing code again. The PC keeps running.',
                          confirmLabel: 'Disconnect',
                          danger: true,
                        );
                        if (ok) await state.signOut();
                      },
                      child: const Text('Use a different server'),
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
}
