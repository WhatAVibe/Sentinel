import 'package:flutter/material.dart';
import 'package:provider/provider.dart';
import 'core/app_state.dart';
import 'core/flipper_bridge.dart';
import 'screens/lock_screen.dart';
import 'screens/pair_screen.dart';
import 'screens/pending_confirmation.dart';
import 'screens/shell.dart';
import 'theme.dart';
import 'widgets.dart';

class SentinelApp extends StatefulWidget {
  const SentinelApp({super.key});

  @override
  State<SentinelApp> createState() => _SentinelAppState();
}

class _SentinelAppState extends State<SentinelApp> with WidgetsBindingObserver {
  final AppState _state = AppState();
  late final FlipperBridge _flipper = FlipperBridge(() => _state.session);
  DateTime? _backgroundedAt;

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addObserver(this);
    _state.boot();
  }

  @override
  void dispose() {
    WidgetsBinding.instance.removeObserver(this);
    _flipper.dispose();
    _state.dispose();
    super.dispose();
  }

  @override
  void didChangeAppLifecycleState(AppLifecycleState lifecycle) {
    if (lifecycle == AppLifecycleState.paused || lifecycle == AppLifecycleState.inactive) {
      _state.clearShotCache();
      _backgroundedAt ??= DateTime.now();
      return;
    }
    if (lifecycle == AppLifecycleState.resumed) {
      final at = _backgroundedAt;
      _backgroundedAt = null;
      if (at != null &&
          DateTime.now().difference(at).inSeconds >= 60 &&
          _state.paired &&
          _state.biometricEnabled &&
          !_state.locked) {
        _state.lockApp();
      }
    }
  }

  @override
  Widget build(BuildContext context) {
    return MultiProvider(
      providers: [
        ChangeNotifierProvider<AppState>.value(value: _state),
        ChangeNotifierProvider<FlipperBridge>.value(value: _flipper),
      ],
      child: MaterialApp(
        title: 'Sentinel',
        debugShowCheckedModeBanner: false,
        theme: buildTheme(),
        home: const _Root(),
      ),
    );
  }
}

class _Root extends StatelessWidget {
  const _Root();

  @override
  Widget build(BuildContext context) {
    final state = context.watch<AppState>();
    if (state.booting) return const _Splash();
    if (!state.paired) return const PairScreen();
    if (state.pendingConfirmation) return const PendingConfirmationScreen();
    if (state.locked) return const LockScreen();
    return const Shell();
  }
}

class _Splash extends StatelessWidget {
  const _Splash();

  @override
  Widget build(BuildContext context) {
    return const Scaffold(
      backgroundColor: C.bg,
      body: Center(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            _SplashMark(),
            SizedBox(height: 20),
            Text('Sentinel', style: TextStyle(fontSize: 20, fontWeight: FontWeight.w600)),
            SizedBox(height: 6),
            Text('securing your session', style: TextStyle(fontSize: 12, color: C.muted)),
          ],
        ),
      ),
    );
  }
}

class _SplashMark extends StatelessWidget {
  const _SplashMark();

  @override
  Widget build(BuildContext context) {
    return const BrandLogo(size: 62, radius: 18);
  }
}
