import 'dart:async';

import 'package:flutter/foundation.dart';

import '../models.dart';
import 'api.dart';
import 'biometrics.dart';
import 'diagnostics.dart';
import 'notifier.dart';
import 'store.dart';
import 'totp.dart';
import 'widgets_bridge.dart';

class AppState extends ChangeNotifier {
  final SessionStore store = SessionStore();

  Session? session;
  List<Session> sessions = [];
  SentinelClient? client;
  Device? device;
  List<ActivityEvent> events = [];
  List<Shot> shots = [];
  bool eventsHasMore = false;
  bool eventsLoadingOlder = false;
  bool shotsHasMore = false;
  bool shotsLoadingOlder = false;

  bool booting = true;
  bool locked = true;
  bool connected = false;
  bool relayOnline = false;
  bool socketLive = false;
  bool pendingConfirmation = false;
  bool busy = false;
  bool biometricEnabled = true;
  bool canUseBiometrics = false;
  bool hasPin = false;
  bool widgetAllowWithoutUnlock = false;
  bool widgetShowCode = true;
  String? lastError;
  String relayVersion = '';
  final Map<String, bool> notifications = {
    'security': true,
    'power': true,
    'message': true,
    'screenshot': true,
    'clipboard': true,
    'other': true,
  };

  StreamSubscription<PushEvent>? _subscription;
  Timer? _poll;
  Timer? _confirmPoll;
  int _lastEventId = 0;

  bool get paired => session != null;

  Future<void> boot() async {
    await Notifier.init();
    sessions = await store.loadSessions();
    if (sessions.isNotEmpty) {
      final activeId = await store.loadActiveId();
      session = sessions.firstWhere(
        (s) => s.deviceId == activeId,
        orElse: () => sessions.first,
      );
      if (session!.uid.isEmpty) {
        session = session!.withUid(newMobileUid());
        final index = sessions.indexWhere((s) => s.deviceId == session!.deviceId);
        if (index >= 0) sessions[index] = session!;
        await store.saveSessions(sessions);
      }
    }
    biometricEnabled = await store.biometricEnabled();
    hasPin = await store.hasPin();
    canUseBiometrics = await Biometrics.available();
    widgetAllowWithoutUnlock = await store.widgetAllowWithoutUnlock();
    widgetShowCode = await store.widgetShowCode();
    for (final key in notifications.keys) {
      notifications[key] = await store.notificationEnabled(key);
    }
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
    _confirmPoll?.cancel();
    _confirmPoll = null;
    pendingConfirmation = false;
    session = value;
    device = null;
    events = [];
    shots = [];
    eventsHasMore = false;
    shotsHasMore = false;
    connected = false;
    _lastEventId = 0;
    if (persist) {
      sessions.removeWhere((s) => s.deviceId == value.deviceId);
      sessions.add(value);
      await store.saveSessions(sessions);
      await store.saveActiveId(value.deviceId);
    }
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
    unawaited(syncWidgets());
    notifyListeners();
  }

  Future<void> syncWidgets() async {
    final current = session;
    if (current == null || current.isDirect || current.token.isEmpty) {
      await SentinelWidgets.clear();
      return;
    }
    await SentinelWidgets.configure(
      baseUrl: current.baseUrl,
      token: current.token,
      deviceId: current.deviceId,
      name: current.displayName.isEmpty ? current.deviceName : current.displayName,
      totp: current.totpSecret,
      allowWithoutUnlock: widgetAllowWithoutUnlock,
      showCode: widgetShowCode,
    );
  }

  Future<void> setWidgetAllowWithoutUnlock(bool value) async {
    widgetAllowWithoutUnlock = value;
    await store.setWidgetAllowWithoutUnlock(value);
    await syncWidgets();
    notifyListeners();
  }

  Future<void> setWidgetShowCode(bool value) async {
    widgetShowCode = value;
    await store.setWidgetShowCode(value);
    await syncWidgets();
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
      try {
        relayVersion = await current.fetchRelayVersion();
      } catch (_) {
        // version display is best-effort
      }
      final fetched = await current.fetchEvents(since: 0);
      events = fetched.reversed.toList();
      eventsHasMore = fetched.length >= 200;
      if (events.isNotEmpty) _lastEventId = events.first.id;
      shots = await current.fetchScreenshots();
      shotsHasMore = shots.length >= 120;
      notifyListeners();
    } catch (error) {
      if (error is ApiError &&
          (error.code == 'invalid_mobile_token' || error.code == 'unauthorized')) {
        await handleServerUnpair();
        return;
      }
      if (isPendingConfirmation(error)) {
        awaitConfirmation();
        return;
      }
      relayOnline = false;
      lastError = describeError(error);
      notifyListeners();
    }
  }

  static bool isPendingConfirmation(Object error) =>
      error is ApiError && error.code == 'pending_confirmation';

  void awaitConfirmation() {
    pendingConfirmation = true;
    relayOnline = true;
    lastError = null;
    _confirmPoll ??= Timer.periodic(const Duration(seconds: 3), (_) => checkConfirmation());
    notifyListeners();
  }

  Future<void> checkConfirmation() async {
    final current = client;
    if (current == null) return;
    try {
      if (!await current.fetchConfirmed()) return;
      _confirmPoll?.cancel();
      _confirmPoll = null;
      pendingConfirmation = false;
      Notifier.show('Device confirmed', 'Your PC confirmed this device. Welcome back.');
      await _initialLoad();
      if (pendingConfirmation) notifyListeners();
    } catch (_) {
      // keep waiting; the relay may be briefly unreachable
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
              item.category == 'power' ||
              item.category == 'screenshot') {
            final key = notifications.containsKey(item.category) ? item.category : 'other';
            if (notifications[key] ?? true) {
              Notifier.show(item.title, item.detailText.isEmpty ? item.title : item.detailText);
            }
          }
        }
        if (events.length > 5000) events = events.sublist(0, 5000);
        events.sort((a, b) => b.id.compareTo(a.id));
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
        if (shots.length > 500) shots = shots.sublist(0, 500);
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
      case 'unpaired':
        DiagLog.instance.add('session', 'PC unpaired from the relay');
        Notifier.show('Sentinel unpaired', 'This PC was unpaired. Pair again to continue.');
        unawaited(handleServerUnpair());
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
      if (error is ApiError &&
          (error.code == 'invalid_mobile_token' || error.code == 'unauthorized')) {
        await handleServerUnpair();
        return;
      }
      if (isPendingConfirmation(error)) {
        awaitConfirmation();
        return;
      }
      relayOnline = false;
      lastError = describeError(error);
    }
    notifyListeners();
  }

  Future<void> handleServerUnpair() async {
    await store.clearSessions();
    await SentinelWidgets.clear();
    _confirmPoll?.cancel();
    _confirmPoll = null;
    pendingConfirmation = false;
    _teardownClient();
    sessions = [];
    session = null;
    device = null;
    events = [];
    shots = [];
    connected = false;
    relayOnline = false;
    locked = false;
    notifyListeners();
  }

  Future<void> loadOlderEvents() async {
    final current = client;
    if (current == null || eventsLoadingOlder || !eventsHasMore || events.isEmpty) return;
    eventsLoadingOlder = true;
    notifyListeners();
    try {
      final older = await current.fetchEvents(before: events.last.id);
      final known = events.map((e) => e.id).toSet();
      final fresh = older.where((e) => !known.contains(e.id)).toList();
      events = [...fresh.reversed, ...events];
      eventsHasMore = older.length >= 200;
      lastError = null;
    } catch (error) {
      lastError = describeError(error);
    } finally {
      eventsLoadingOlder = false;
      notifyListeners();
    }
  }

  Future<void> loadOlderShots() async {
    final current = client;
    if (current == null || shotsLoadingOlder || !shotsHasMore || shots.isEmpty) return;
    shotsLoadingOlder = true;
    notifyListeners();
    try {
      final older = await current.fetchScreenshots(beforeTs: shots.last.ts);
      final known = shots.map((s) => s.id).toSet();
      shots = [...shots, ...older.where((s) => !known.contains(s.id))];
      shotsHasMore = older.length >= 120;
      lastError = null;
    } catch (error) {
      lastError = describeError(error);
    } finally {
      shotsLoadingOlder = false;
      notifyListeners();
    }
  }

  Future<void> loadShots() async {
    final current = client;
    if (current == null) return;
    try {
      shots = await current.fetchScreenshots();
      shotsHasMore = shots.length >= 120;
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

  Future<CommandResult> copyToPc(String text) => command('clipboard', args: {'text': text});

  Future<void> renameDeviceOnPc(String name) async {
    final current = client;
    if (current == null || session?.isDirect == true) return;
    try {
      await current.renameDevice(name);
      device = await current.fetchDevice();
      relayOnline = true;
      notifyListeners();
    } catch (_) {
      // keep the local alias even when the relay is unreachable
    }
  }

  Future<void> updateSettings({bool? require2fa, bool? protectPower}) async {
    final current = client;
    if (current == null) return;
    await current.updateSettings(require2fa: require2fa, protectPower: protectPower);
    await refreshDevice();
  }

  Future<List<int>> exportHistory(String what, String format) async {
    final current = client;
    if (current == null) throw const ApiError(400, 'not_paired');
    return current.exportData(what, format);
  }

  Future<void> unpair() async {
    final current = client;
    final removedId = session?.deviceId;
    try {
      await current?.removeDevice();
    } catch (_) {
      // server may be unreachable; local cleanup still applies
    }
    sessions.removeWhere((s) => s.deviceId == removedId);
    await store.saveSessions(sessions);
    _teardownClient();
    device = null;
    events = [];
    shots = [];
    connected = false;
    relayOnline = false;
    pendingConfirmation = false;
    if (sessions.isEmpty) {
      await store.saveActiveId(null);
      session = null;
      await SentinelWidgets.clear();
    } else {
      session = sessions.first;
      await store.saveActiveId(session!.deviceId);
      await attach(session!, persist: false);
      return;
    }
    notifyListeners();
  }

  Future<void> switchTo(Session value) => attach(value);

  Future<void> renameSession(String deviceId, String localName) async {
    final index = sessions.indexWhere((s) => s.deviceId == deviceId);
    if (index < 0) return;
    sessions[index] = sessions[index].withLocalName(localName.trim());
    await store.saveSessions(sessions);
    if (session?.deviceId == deviceId) {
      session = sessions[index];
      DiagLog.instance.add('session', 'Renamed PC to ${session!.displayName}');
      notifyListeners();
    }
  }

  Future<void> signOut() async {
    await store.clearSessions();
    _teardownClient();
    sessions = [];
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

  Future<void> setNotificationEnabled(String key, bool value) async {
    notifications[key] = value;
    await store.setNotificationEnabled(key, value);
    notifyListeners();
  }

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
    _confirmPoll?.cancel();
    _confirmPoll = null;
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
