import 'dart:async';
import 'dart:convert';

import 'package:flutter/foundation.dart';
import 'package:flutter_blue_plus/flutter_blue_plus.dart';
import 'package:permission_handler/permission_handler.dart';

import '../models.dart';
import 'api.dart';
import 'totp.dart';

/// Bridges a Flipper Zero running the Sentinel app to the relay by using the
/// phone as the network. The Flipper sends short command lines; this class
/// executes them with the active Sentinel session and streams state back.
class FlipperBridge extends ChangeNotifier {
  FlipperBridge(this._sessionProvider);

  static final Guid serviceUuid = Guid('8fe5b3d5-2e7f-4a98-2a48-7acc60fe0000');
  static final Guid rxUuid = Guid('19ed82ae-ed21-4c9d-4145-228e62fe0000');
  static final Guid txUuid = Guid('19ed82ae-ed21-4c9d-4145-228e61fe0000');

  final Session? Function() _sessionProvider;

  bool scanning = false;
  bool connected = false;
  bool connecting = false;
  bool reconnecting = false;
  String? error;
  String? statusText;
  BluetoothDevice? device;
  List<ScanResult> results = [];

  BluetoothCharacteristic? _rx;
  StreamSubscription<List<int>>? _txSub;
  StreamSubscription<BluetoothConnectionState>? _connSub;
  StreamSubscription<List<ScanResult>>? _scanSub;
  StreamSubscription<bool>? _scanStateSub;
  Timer? _poll;
  Timer? _watchdog;
  final List<int> _lineBuffer = [];
  bool _busy = false;
  bool _autoReconnect = false;
  bool _everConnected = false;
  int _attempts = 0;
  DateTime _lastRx = DateTime.now();
  bool _teardownBecauseUserAsked = false;

  static const Duration _rxTimeout = Duration(seconds: 22);

  bool get supported => !kIsWeb;

  static bool isFlipper(BluetoothDevice device) {
    final name = device.advName.toLowerCase();
    return name.contains('flipper') || name.contains('vibeflip') || name.contains('flip_');
  }

  Future<void> scan() async {
    error = null;
    results = [];
    notifyListeners();

    if (!await _ensurePermissions()) return;

    try {
      if (await FlutterBluePlus.isSupported == false) {
        error = 'This device does not support Bluetooth LE.';
        notifyListeners();
        return;
      }
      if (await FlutterBluePlus.adapterState.first != BluetoothAdapterState.on) {
        try {
          await FlutterBluePlus.turnOn();
        } catch (_) {
          error = 'Turn Bluetooth on and try again.';
          notifyListeners();
          return;
        }
      }

      scanning = true;
      notifyListeners();
      _scanSub?.cancel();
      _scanSub = FlutterBluePlus.scanResults.listen((found) {
        // Keep the order stable and put Flippers on top: the list reorders on
        // every RSSI update otherwise, so a tap can land on the wrong device.
        results = found.toList()
          ..sort((a, b) {
            final aFlip = isFlipper(a.device);
            final bFlip = isFlipper(b.device);
            if (aFlip != bFlip) return aFlip ? -1 : 1;
            return a.device.remoteId.str.compareTo(b.device.remoteId.str);
          });
        notifyListeners();
      });
      _scanStateSub?.cancel();
      _scanStateSub = FlutterBluePlus.isScanning.listen((value) {
        scanning = value;
        notifyListeners();
      });
      await FlutterBluePlus.startScan(timeout: const Duration(seconds: 12));
    } catch (e) {
      error = _describe(e);
    } finally {
      scanning = false;
      notifyListeners();
    }
  }

  Future<void> stopScan() async {
    await FlutterBluePlus.stopScan();
    scanning = false;
    notifyListeners();
  }

  Future<bool> _ensurePermissions() async {
    try {
      final statuses = await [
        Permission.bluetoothScan,
        Permission.bluetoothConnect,
        Permission.locationWhenInUse,
      ].request();
      final scanOk = statuses[Permission.bluetoothScan]?.isGranted ?? false;
      final connectOk = statuses[Permission.bluetoothConnect]?.isGranted ?? false;
      if (scanOk && connectOk) return true;
      if (statuses[Permission.locationWhenInUse]?.isGranted ?? false) return true;
      error = 'Bluetooth permissions are required to find the Flipper.';
      notifyListeners();
      return false;
    } catch (e) {
      error = _describe(e);
      notifyListeners();
      return false;
    }
  }

  Future<void> connect(BluetoothDevice target) async {
    if (connecting || connected) return;
    if (device?.remoteId.str != target.remoteId.str) _everConnected = false;
    _autoReconnect = true;
    _attempts = 0;
    await _connectTo(target);
  }

  /// Manual retry, e.g. after the phone was out of range.
  Future<void> reconnectNow() async {
    final target = device;
    if (target == null) {
      error = 'Pick the Flipper in the scan list first.';
      notifyListeners();
      return;
    }
    _autoReconnect = true;
    _attempts = 0;
    await _connectTo(target);
  }

  Future<void> _connectTo(BluetoothDevice target) async {
    if (connecting) return;
    connecting = true;
    _appMissing = false;
    _teardownBecauseUserAsked = false;
    error = null;
    statusText =
        '${reconnecting ? 'Reconnecting to' : 'Connecting to'} ${target.advName.isEmpty ? target.remoteId : target.advName}…';
    notifyListeners();

    try {
      await FlutterBluePlus.stopScan();
      device = target;
      await target.connect(license: License.nonprofit, timeout: const Duration(seconds: 25));
      try {
        await target.createBond(timeout: 25);
      } catch (_) {
        // Already bonded, or the platform handles the prompt on first write.
      }

      final services = await target.discoverServices();
      BluetoothService? serial;
      for (final service in services) {
        if (service.uuid == serviceUuid) {
          serial = service;
          break;
        }
      }
      if (serial == null) {
        throw StateError('Sentinel Flipper app is not advertising its serial service.');
      }

      _rx = serial.characteristics.firstWhere((c) => c.uuid == rxUuid);
      final tx = serial.characteristics.firstWhere((c) => c.uuid == txUuid);
      await tx.setNotifyValue(true, forceIndications: true);
      await _txSub?.cancel();
      _txSub = tx.onValueReceived.listen(_onBytes);

      await _connSub?.cancel();
      _connSub = target.connectionState.listen((state) {
        if (state == BluetoothConnectionState.disconnected) _onDropped();
      });

      connected = true;
      connecting = false;
      reconnecting = false;
      _attempts = 0;
      _everConnected = true;
      _lastRx = DateTime.now();
      statusText = 'Connected to ${target.advName.isEmpty ? 'Flipper' : target.advName}';
      notifyListeners();

      _startTimers();
      // Handshake: the Flipper answers PING with PONG only when its app is
      // running, so a silent link means we landed on the stock firmware profile.
      await _send('INFO|Phone connected');
      await _handshake();
      await sendState();
    } catch (e) {
      error = _describe(e);
      connecting = false;
      reconnecting = false;
      statusText = null;
      notifyListeners();
      _stopTimers();
      _connSub?.cancel();
      _connSub = null;
      _txSub?.cancel();
      _txSub = null;
      _rx = null;
      connected = false;
      try {
        await target.disconnect();
      } catch (_) {}
      if (_everConnected && !_appMissing) {
        _scheduleReconnect();
      } else {
        _autoReconnect = false;
      }
    }
  }

  bool _appMissing = false;
  bool _pongSeen = false;

  static const String _noAppMessage =
      'No answer from the Flipper. Open Sentinel Bridge on it and try again.';

  Future<void> _handshake() async {
    _appMissing = false;
    _pongSeen = false;
    await _send('PING');
    for (var i = 0; i < 25; i++) {
      await Future.delayed(const Duration(milliseconds: 200));
      if (_pongSeen) return;
      if (!connected) break;
    }
    _appMissing = true;
    throw StateError(_noAppMessage);
  }

  void _startTimers() {
    _poll?.cancel();
    _poll = Timer.periodic(const Duration(seconds: 12), (_) => sendState());
    _watchdog?.cancel();
    _watchdog = Timer.periodic(const Duration(seconds: 5), (_) {
      if (!connected) return;
      // The Flipper pings every 10 s; silence means the link is gone even if
      // Android still claims it is connected.
      if (DateTime.now().difference(_lastRx) > _rxTimeout) {
        error = 'The Flipper stopped answering. Reconnecting…';
        notifyListeners();
        _onDropped();
      }
    });
  }

  void _stopTimers() {
    _poll?.cancel();
    _poll = null;
    _watchdog?.cancel();
    _watchdog = null;
  }

  void _onDropped() {
    if (_teardownBecauseUserAsked) return;
    final target = device;
    final wasConnected = connected;
    _stopTimers();
    _connSub?.cancel();
    _connSub = null;
    _txSub?.cancel();
    _txSub = null;
    _rx = null;
    _lineBuffer.clear();
    connected = false;
    connecting = false;
    if (target == null || !_autoReconnect) {
      if (wasConnected) {
        statusText = null;
        notifyListeners();
      }
      return;
    }
    statusText = 'Flipper disconnected. Reconnecting…';
    notifyListeners();
    _scheduleReconnect();
  }

  void _scheduleReconnect() {
    if (!_autoReconnect || device == null) return;
    if (_attempts >= 6) {
      _autoReconnect = false;
      reconnecting = false;
      error = 'Lost the Flipper. Open the app on it and tap Reconnect.';
      statusText = null;
      notifyListeners();
      return;
    }
    _attempts++;
    reconnecting = true;
    error = null;
    notifyListeners();
    final delay = Duration(seconds: _attempts <= 2 ? 2 : (_attempts <= 4 ? 4 : 8));
    Timer(delay, () {
      if (!_autoReconnect || connected || connecting) return;
      final target = device;
      if (target != null) unawaited(_connectTo(target));
    });
  }

  Future<void> disconnect() async {
    _teardownBecauseUserAsked = true;
    _autoReconnect = false;
    reconnecting = false;
    _attempts = 0;
    _stopTimers();
    try {
      await device?.disconnect();
    } catch (_) {}
    _teardown(notify: true);
  }

  void _teardown({bool notify = false}) {
    _stopTimers();
    _connSub?.cancel();
    _connSub = null;
    _txSub?.cancel();
    _txSub = null;
    _rx = null;
    _lineBuffer.clear();
    connected = false;
    connecting = false;
    reconnecting = false;
    statusText = null;
    if (notify) notifyListeners();
  }

  void _onBytes(List<int> bytes) {
    for (final byte in bytes) {
      if (byte == 10 || byte == 13) {
        if (_lineBuffer.isNotEmpty) {
          final line = utf8.decode(_lineBuffer, allowMalformed: true).trim();
          _lineBuffer.clear();
          if (line.isNotEmpty) unawaited(_handleLine(line));
        }
      } else if (_lineBuffer.length < 512) {
        _lineBuffer.add(byte);
      }
    }
  }

  Future<void> _handleLine(String line) async {
    _lastRx = DateTime.now();
    switch (line) {
      case 'PING':
        await _send('PONG');
        break;
      case 'PONG':
        _pongSeen = true;
        break;
      case 'STATUS':
        await sendState();
        break;
      case 'LOCK':
        await _command('lock');
        break;
      case 'SECURE':
        await _command('lock_screen', args: {'message': 'Locked from Flipper'});
        break;
      case 'SHOT':
        await _command('screenshot');
        break;
      case 'UNLOCK':
        await _unlock();
        break;
      case 'AWAY':
        await _away();
        break;
      default:
        if (line.startsWith('PAIR:')) {
          await _send('ERR|Pairing is handled by this phone');
        }
    }
  }

  Future<void> _command(String type, {Map<String, dynamic>? args, String? code}) async {
    if (_busy) return;
    final session = _sessionProvider();
    if (session == null) {
      await _send('ERR|No paired PC in the phone app');
      return;
    }
    _busy = true;
    try {
      final client = createClient(session);
      try {
        await client.sendCommand(type, args: args, code: code);
      } finally {
        client.dispose();
      }
      await _send('INFO|sent $type');
      await Future.delayed(const Duration(milliseconds: 700));
      await sendState();
    } catch (e) {
      await _send('ERR|${_relayError(e)}');
    } finally {
      _busy = false;
    }
  }

  Future<void> _unlock() async {
    final session = _sessionProvider();
    if (session == null) {
      await _send('ERR|No paired PC in the phone app');
      return;
    }
    try {
      final code = totpCode(session.totpSecret);
      if (code.isEmpty) {
        await _send('ERR|No TOTP secret in this session');
        return;
      }
      final client = createClient(session);
      final device = await client.fetchDevice();
      client.dispose();
      final type = device.state.secureLock ? 'unlock_screen' : 'unlock';
      await _command(type, code: code);
    } catch (e) {
      await _send('ERR|${_relayError(e)}');
    }
  }

  Future<void> _away() async {
    final session = _sessionProvider();
    if (session == null) {
      await _send('ERR|No paired PC in the phone app');
      return;
    }
    try {
      final client = createClient(session);
      final device = await client.fetchDevice();
      client.dispose();
      if (device.away) {
        final code = totpCode(session.totpSecret);
        if (code.isEmpty) {
          await _send('ERR|No TOTP secret in this session');
          return;
        }
        await _command('away_disable', code: code);
      } else {
        await _command('away_enable');
      }
    } catch (e) {
      await _send('ERR|${_relayError(e)}');
    }
  }

  Future<void> sendState() async {
    final session = _sessionProvider();
    if (session == null) {
      await _send('ERR|No paired PC in the phone app');
      return;
    }
    try {
      final client = createClient(session);
      final device = await client.fetchDevice();
      client.dispose();
      final secure = device.state.secureLock;
      final code = totpCode(session.totpSecret);
      final seconds = totpSecondsRemaining();
      final name = device.name.replaceAll('|', ' ');
      final sessionState = device.state.session.replaceAll('|', ' ');
      await _send(
        'STATE|${device.online ? 1 : 0}|${device.away ? 1 : 0}|${secure ? 1 : 0}|'
        '$sessionState|$code|$seconds|$name',
      );
      statusText = '${device.name} · ${device.online ? 'online' : 'offline'}';
      error = null;
      notifyListeners();
    } catch (e) {
      error = _relayError(e);
      await _send('ERR|${_relayError(e)}');
      notifyListeners();
    }
  }

  Future<void> _send(String line) async {
    final rx = _rx;
    if (rx == null || !connected) return;
    try {
      await rx.write(utf8.encode('$line\n'), withoutResponse: false);
    } catch (e) {
      error = _describe(e);
      notifyListeners();
    }
  }

  String _relayError(Object error) {
    if (error is ApiError) return error.message;
    return error.toString();
  }

  String _describe(Object error) {
    if (error is FlutterBluePlusException) return error.description ?? error.toString();
    final text = error.toString();
    return text.startsWith('Bad state: ') ? text.substring(11) : text;
  }

  @override
  void dispose() {
    _teardown();
    _scanSub?.cancel();
    _scanStateSub?.cancel();
    unawaited(device?.disconnect());
    super.dispose();
  }
}
