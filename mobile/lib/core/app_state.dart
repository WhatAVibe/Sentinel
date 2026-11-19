import 'dart:async';

import 'package:flutter/foundation.dart';

import '../models.dart';
import 'api.dart';
import 'biometrics.dart';
import 'diagnostics.dart';
import 'notifier.dart';
import 'store.dart';
import 'totp.dart';

class AppState extends ChangeNotifier {
  final SessionStore store = SessionStore();

  Session? session;
  SentinelClient? client;
  Device? device;
  List<ActivityEvent> events = [];
  List<Shot> shots = [];

  bool booting = true;
  bool locked = true;
  bool connected = false;
  bool relayOnline = false;
  bool socketLive = false;
  bool busy = false;
  bool biometricEnabled = true;
  bool canUseBiometrics = false;
  bool hasPin = false;
  String? lastError;

  StreamSubscription<PushEvent>? _subscription;
  Timer? _poll;
  int _lastEventId = 0;

  bool get paired => session != null;

  Future<void> boot() async {
    await Notifier.init();
    session = await store.loadSession();
    if (session != null) {
      if (session!.uid.isEmpty) {
        session = session!.withUid(newMobileUid());
        await store.saveSession(session!);
        DiagLog.instance.add('session', 'Generated stable phone id');
      }
    }
    biometricEnabled = await store.biometricEnabled();
    hasPin = await store.hasPin();
    canUseBiometrics = await Biometrics.available();
    booting = false;

    DiagLog.instance.add(
      'session',
      session == null ? 'No saved session — showing pairing' : 'Loaded ${session!.baseUrl}',
    );

    if (session != null && session!.token.isNotEmpty) {
      await attach(session!, connect: true, persist: false);
    }
    notifyListeners();
  }

  Future<void> attach(Session value, {bool connect = true, bool persist = true}) async {
    _teardownClient();
    session = value;
    if (persist) await store.saveSession(value);
    DiagLog.instance.add('session', 'Attach ${value.baseUrl} (${value.mode})');
    if (!connect) {
      notifyListeners();
      return;
    }

    final created = createClient(value);
    client = created;
    _subscription = created.stream.listen(_onPush);
    unawaited(_initialLoad());
    _poll = Timer.periodic(const Duration(seconds: 15), (_) => refreshDevice());
    notifyListeners();
  }

  Future<void> _initialLoad() async {
    try {
      final current = client;
      if (current == null) return;
      device = await current.fetchDevice();
      relayOnline = true;
      connected = device?.online ?? false;
      lastError = null;
      final fetched = await current.fetchEvents(since: 0);
      events = fetched.reversed.toList();
      if (events.isNotEmpty) _lastEventId = events.first.id;
      shots = await current.fetchScreenshots();
      notifyListeners();
    } catch (error) {
      relayOnline = false;
      lastError = describeError(error);
      notifyListeners();
    }
  }

  void _onPush(PushEvent event) {
    switch (event.type) {
      case 'state':
        final incoming = event.data as Device;
        relayOnline = true;
        if (connected != incoming.online) {
          DiagLog.instance.add('agent', incoming.online ? 'PC online' : 'PC offline');
        }
        device = incoming;
        connected = incoming.online;
        break;
      case 'events':
        final incoming = (event.data as List).cast<ActivityEvent>();
        for (final item in incoming) {
          if (events.any((e) => e.id == item.id)) continue;
          events.insert(0, item);
          if (item.id > _lastEventId) _lastEventId = item.id;
          if (item.severity == 'warning' ||
              item.category == 'security' ||
              item.category == 'message' ||
              item.category == 'power') {
            Notifier.show(item.title, item.detailText);
          }
        }
        if (events.length > 800) events = events.sublist(0, 800);
        if (incoming.isNotEmpty) {
          DiagLog.instance.add('agent', '+${incoming.length} events');
        }
        break;
      case 'message':
        final message = asMap(event.data);
        final reply = asString(message['reply']);
        DiagLog.instance.add('message', reply.isEmpty ? 'message ${asString(message['status'])}' : 'reply: $reply');
        if (reply.isNotEmpty) {
          Notifier.show('Reply from your PC', reply);
        }
        break;
      case 'shots':
        final incoming = (event.data as List).cast<Shot>();
        for (final shot in incoming) {
          shots.removeWhere((s) => s.id == shot.id);
          shots.insert(0, shot);
        }
        if (shots.length > 200) shots = shots.sublist(0, 200);
        DiagLog.instance.add('agent', '+${incoming.length} screenshots');
        break;
      case 'connected':
        socketLive = true;
        relayOnline = true;
        DiagLog.instance.add('socket', 'live channel connected');
        break;
      case 'disconnected':
        socketLive = false;
        DiagLog.instance.add('socket', 'live channel lost — falling back to polling', error: true);
        break;
      case 'command':
        final command = asMap(event.data);
        DiagLog.instance.add('agent', 'command ${asString(command['type'])} · ${asString(command['status'])}');
        break;
      case 'paired':
        DiagLog.instance.add('session', 'server signalled pairing');
        unawaited(refreshDevice());
        break;
    }
    notifyListeners();
  }

  Future<void> refreshDevice() async {
    final current = client;
    if (current == null) return;
    try {
      device = await current.fetchDevice();
      relayOnline = true;
      connected = device?.online ?? false;
      lastError = null;
    } catch (error) {
      relayOnline = false;
      lastError = describeError(error);
    }
    notifyListeners();
  }

  Future<void> loadShots() async {
    final current = client;
    if (current == null) return;
    try {
      shots = await current.fetchScreenshots();
      notifyListeners();
    } catch (_) {
      // keep previous gallery
    }
  }

  Future<CommandResult> command(String type, {Map<String, dynamic>? args, String? code}) async {
    final current = client;
    if (current == null) return const CommandResult(ok: false, error: 'not_paired');
    busy = true;
    notifyListeners();
    try {
      final result = await current.sendCommand(type, args: args, code: code);
      if (!result.ok) lastError = result.error;
      if (result.ok) {
        if (type == 'screenshot') {
          unawaited(Future.delayed(const Duration(seconds: 2), loadShots));
        }
        if (type == 'away_enable' || type == 'away_disable' || type == 'unlock') {
          unawaited(refreshDevice());
        }
      }
      return result;
    } catch (error) {
      DiagLog.instance.add('command', '$type failed: ${describeError(error)}', error: true);
      return CommandResult(ok: false, error: describeError(error));
    } finally {
      busy = false;
      notifyListeners();
    }
  }

  Future<CommandResult> sendMessage({
    required String body,
    String title = 'Message from your phone',
    String kind = 'toast',
    bool allowReply = false,
  }) async {
    final current = client;
    if (current == null) return const CommandResult(ok: false, error: 'not_paired');
    busy = true;
    notifyListeners();
    try {
      final result = await current.sendMessage(
        body: body,
        title: title,
        kind: kind,
        allowReply: allowReply,
      );
      if (!result.ok) lastError = result.error;
      return result;
    } catch (error) {
      return CommandResult(ok: false, error: describeError(error));
    } finally {
      busy = false;
      notifyListeners();
    }
  }

  Future<CommandResult> disarm(String code) async {    final current = client;
    if (current == null) return const CommandResult(ok: false, error: 'not_paired');
    busy = true;
    notifyListeners();
    try {
      final result = await current.unlock(code, action: 'unlock');
      if (result.ok) unawaited(refreshDevice());
      return result;
    } catch (error) {
      return CommandResult(ok: false, error: describeError(error));
    } finally {
      busy = false;
      notifyListeners();
    }
  }

  Future<bool> verifyCode(String code) async {
    final current = client;
    if (current == null || code.length != 6) return false;
    try {
      return await current.verifyCode(code);
    } catch (_) {
      return false;
    }
  }

  Future<void> updateSettings({bool? require2fa, bool? protectPower}) async {
    final current = client;
    if (current == null) return;
    await current.updateSettings(require2fa: require2fa, protectPower: protectPower);
    await refreshDevice();
  }

  Future<void> unpair() async {
    final current = client;
    try {
      await current?.removeDevice();
    } catch (_) {
      // server may be unreachable; local cleanup still applies
    }
    await store.clearSession();
    _teardownClient();
    session = null;
    device = null;
    events = [];
    shots = [];
    connected = false;
    relayOnline = false;
    notifyListeners();
  }

  Future<void> signOut() async {
    await store.clearSession();
    _teardownClient();
    session = null;
    device = null;
    events = [];
    shots = [];
    connected = false;
    relayOnline = false;
    notifyListeners();
  }

  void unlockApp() {
    locked = false;
    notifyListeners();
  }

  void lockApp() {
    locked = true;
    notifyListeners();
  }

  String get liveCode => totpCode(session?.totpSecret ?? '');

  int get codeSeconds => totpSecondsRemaining();

  String imageUrl(Shot shot) {
    if (shot.url.startsWith('http')) return shot.url;
    final base = session?.baseUrl ?? '';
    return '$base${shot.url}';
  }

  Map<String, String> get imageHeaders => {
        if (session != null) 'Authorization': 'Bearer ${session!.token}',
      };

  Future<void> setBiometricEnabled(bool value) async {
    biometricEnabled = value;
    await store.setBiometricEnabled(value);
    notifyListeners();
  }

  Future<void> savePin(String pin) async {
    await store.savePin(pin);
    hasPin = true;
    notifyListeners();
  }

  Future<bool> verifyPin(String pin) => store.verifyPin(pin);

  void _teardownClient() {
    _subscription?.cancel();
    _subscription = null;
    _poll?.cancel();
    _poll = null;
    client?.dispose();
    client = null;
    socketLive = false;
    relayOnline = false;
  }

  @override
  void dispose() {
    _teardownClient();
    super.dispose();
  }
}

String describeError(Object error) {
  if (error is ApiError) return error.message;
  if (error is TimeoutException) return 'The server took too long to respond.';
  return 'Cannot reach the server.';
}
