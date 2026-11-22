import 'dart:async';

import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../core/app_state.dart';
import '../theme.dart';
import '../widgets.dart';

class PendingConfirmationScreen extends StatefulWidget {
  const PendingConfirmationScreen({super.key});

  @override
  State<PendingConfirmationScreen> createState() => _PendingConfirmationScreenState();
}

class _PendingConfirmationScreenState extends State<PendingConfirmationScreen> {
  late final Timer _timer;
  int _seconds = 0;

  @override
  void initState() {
    super.initState();
    _seconds = context.read<AppState>().codeSeconds;
    _timer = Timer.periodic(const Duration(seconds: 1), (_) {
      if (mounted) setState(() => _seconds = context.read<AppState>().codeSeconds);
    });
  }

  @override
  void dispose() {
    _timer.cancel();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final state = context.watch<AppState>();
    final name = state.device?.name ?? state.session?.deviceName ?? 'your PC';
    final code = state.liveCode;

    return Scaffold(
      backgroundColor: C.bg,
      body: SafeArea(
        child: Center(
          child: SingleChildScrollView(
            padding: const EdgeInsets.fromLTRB(26, 24, 26, 24),
            child: Column(
              mainAxisSize: MainAxisSize.min,
              children: [
                const BrandLogo(size: 54, radius: 16),
                const SizedBox(height: 18),
                const Text('Waiting for your PC',
                    style: TextStyle(fontSize: 19, fontWeight: FontWeight.w600)),
                const SizedBox(height: 6),
                Text(
                  'This device asked to link to $name. Approve it on the PC and type this code there.',
                  textAlign: TextAlign.center,
                  style: const TextStyle(fontSize: 12, color: C.muted, height: 1.45),
                ),
                const SizedBox(height: 22),
                Container(
                  width: double.infinity,
                  padding: const EdgeInsets.symmetric(vertical: 18),
                  decoration: BoxDecoration(
                    color: C.surface,
                    borderRadius: BorderRadius.circular(16),
                    border: Border.all(color: C.border),
                  ),
                  child: Column(
                    children: [
                      Text(
                        code,
                        style: const TextStyle(
                          fontSize: 34,
                          fontWeight: FontWeight.w700,
                          letterSpacing: 8,
                          color: C.accent,
                        ),
                      ),
                      const SizedBox(height: 4),
                      Text('refreshes in ${_seconds}s',
                          style: const TextStyle(fontSize: 10.5, color: C.muted)),
                    ],
                  ),
                ),
                const SizedBox(height: 20),
                const SizedBox(
                  width: 22,
                  height: 22,
                  child: CircularProgressIndicator(strokeWidth: 2.2),
                ),
                const SizedBox(height: 10),
                const Text('The pairing stays pending until the PC confirms it.',
                    textAlign: TextAlign.center,
                    style: TextStyle(fontSize: 11, color: C.muted)),
                if (state.lastError != null) ...[
                  const SizedBox(height: 8),
                  Text(state.lastError!,
                      textAlign: TextAlign.center,
                      style: const TextStyle(fontSize: 11, color: C.warning)),
                ],
                const SizedBox(height: 22),
                SizedBox(
                  width: double.infinity,
                  child: FilledButton(
                    onPressed: () => state.checkConfirmation(),
                    child: const Text('Check again'),
                  ),
                ),
                const SizedBox(height: 8),
                TextButton(
                  onPressed: () => state.unpair(),
                  child: const Text('Cancel pairing',
                      style: TextStyle(fontSize: 12, color: C.muted)),
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}
