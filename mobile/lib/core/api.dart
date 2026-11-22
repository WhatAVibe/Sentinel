import 'dart:async';
import 'dart:convert';

import 'package:http/http.dart' as http;
import 'package:web_socket_channel/web_socket_channel.dart';

import '../models.dart';
import 'diagnostics.dart';

String normalizeBaseUrl(String raw) {
  var value = raw.trim();
  if (value.isEmpty) return '';
  if (!value.startsWith('http://') && !value.startsWith('https://')) {
    value = 'http://$value';
  }
  while (value.endsWith('/')) {
    value = value.substring(0, value.length - 1);
  }
  return value;
}

class _Http {
  final String baseUrl;
  final String? token;
  final http.Client client;

  _Http(this.baseUrl, this.token, this.client);

  Map<String, String> get headers => {
        'Content-Type': 'application/json',
        if (token != null && token!.isNotEmpty) 'Authorization': 'Bearer $token',
        if (sentinelDeviceName.isNotEmpty) 'X-Device-Name': sentinelDeviceName,
        'X-App-Version': sentinelAppVersion,
      };

  Uri uri(String path) => Uri.parse('$baseUrl$path');

  Map<String, dynamic> _decode(http.Response response) {
    Map<String, dynamic>? body;
    try {
      final decoded = jsonDecode(response.body);
      if (decoded is Map<String, dynamic>) body = decoded;
    } catch (_) {
      body = null;
    }
    if (response.statusCode >= 400) {
      throw ApiError(response.statusCode, asString(body?['error'], 'http_${response.statusCode}'));
    }
    return body ?? <String, dynamic>{};
  }

  Future<Map<String, dynamic>> get(String path) => _run('GET', path, () async {
        final response = await client.get(uri(path), headers: headers).timeout(const Duration(seconds: 12));
        return response;
      });

  Future<Map<String, dynamic>> post(String path, Map<String, dynamic> body) => _run('POST', path, () async {
        final response = await client
            .post(uri(path), headers: headers, body: jsonEncode(body))
            .timeout(const Duration(seconds: 45));
        return response;
      });

  Future<Map<String, dynamic>> patch(String path, Map<String, dynamic> body) => _run('PATCH', path, () async {
        final response = await client
            .patch(uri(path), headers: headers, body: jsonEncode(body))
            .timeout(const Duration(seconds: 12));
        return response;
      });

  Future<Map<String, dynamic>> delete(String path) => _run('DELETE', path, () async {
        final response = await client.delete(uri(path), headers: headers).timeout(const Duration(seconds: 12));
        return response;
      });

  Future<Map<String, dynamic>> _run(
    String method,
    String path,
    Future<http.Response> Function() call,
  ) async {
    final watch = Stopwatch()..start();
    try {
      final response = await call();
      DiagLog.instance.add('http', '$method $path → ${response.statusCode} (${watch.elapsedMilliseconds} ms)');
      return _decode(response);
    } catch (error) {
      DiagLog.instance.add('http', '$method $path failed (${watch.elapsedMilliseconds} ms): $error',
          error: true);
      rethrow;
    }
  }
}

abstract class SentinelClient {
  Session get session;
  String get baseUrl;
  bool get isDirect;
  Stream<PushEvent> get stream;
  Future<Device> fetchDevice();
  Future<List<ActivityEvent>> fetchEvents({int since = 0, int before = 0, int limit = 200});
  Future<List<Shot>> fetchScreenshots({int beforeTs = 0, int limit = 120});
  Future<CommandResult> sendCommand(String type, {Map<String, dynamic>? args, String? code});
  Future<CommandResult> sendMessage({
    required String body,
    String title,
    String kind,
    bool allowReply,
  });
  Future<CommandResult> unlock(String code, {String action = 'unlock'});
  Future<bool> verifyCode(String code);
  Future<List<PhoneInfo>> fetchPhones();
  Future<void> removePhone(String id);
  Future<void> updateSettings({bool? require2fa, bool? protectPower});
  Future<void> renameDevice(String name);
  Future<String> fetchRelayVersion();
  Future<void> removeDevice();
  void dispose();
}

class RelayClient implements SentinelClient {
  @override
  final Session session;
  final http.Client _http = http.Client();
  late final _Http _api = _Http(session.baseUrl, session.token, _http);
  final StreamController<PushEvent> _controller = StreamController<PushEvent>.broadcast();
  WebSocketChannel? _channel;
  Timer? _retry;
  int _attempt = 0;
  bool _disposed = false;

  RelayClient(this.session);

  @override
  String get baseUrl => session.baseUrl;

  @override
  bool get isDirect => false;

  String get _deviceId => session.deviceId;

  @override
  Stream<PushEvent> get stream {
    _connect();
    return _controller.stream;
  }

  void _connect() {
    if (_disposed || _channel != null) return;
    final wsBase = session.baseUrl.replaceFirst(RegExp('^http'), 'ws');
    final uri = Uri.parse('$wsBase/ws/client?token=${Uri.encodeComponent(session.token)}');
    try {
      final channel = WebSocketChannel.connect(uri);
      _channel = channel;
      channel.ready.then((_) {
        _attempt = 0;
        _controller.add(const PushEvent('connected'));
      }).catchError((_) {
        _reconnect();
      });
      channel.stream.listen(
        (raw) {
          try {
            final decoded = jsonDecode(raw as String);
            if (decoded is! Map<String, dynamic>) return;
            final type = asString(decoded['type']);
            switch (type) {
              case 'state':
                _controller.add(PushEvent('state', Device.fromJson(asMap(decoded['device']))));
                break;
              case 'events':
                _controller.add(PushEvent(
                  'events',
                  (decoded['events'] as List? ?? const [])
                      .map((e) => ActivityEvent.fromJson(asMap(e)))
                      .toList(),
                ));
                break;
              case 'screenshots':
                _controller.add(PushEvent(
                  'shots',
                  (decoded['screenshots'] as List? ?? const [])
                      .map((e) => Shot.fromJson(asMap(e)))
                      .toList(),
                ));
                break;
              case 'command':
                _controller.add(PushEvent('command', asMap(decoded['command'])));
                break;
              case 'message':
                _controller.add(PushEvent('message', asMap(decoded['message'])));
                break;
              case 'paired':
                _controller.add(const PushEvent('paired'));
                break;
            }
          } catch (_) {
            // ignore malformed frames
          }
        },
        onError: (_) => _reconnect(),
        onDone: _reconnect,
        cancelOnError: true,
      );
    } catch (_) {
      _reconnect();
    }
  }

  void _reconnect() {
    _channel = null;
    if (_disposed) return;
    _controller.add(const PushEvent('disconnected'));
    _retry?.cancel();
    const delays = [1, 2, 4, 8, 15, 30];
    final delay = delays[_attempt.clamp(0, delays.length - 1)];
    _attempt++;
    _retry = Timer(Duration(seconds: delay), _connect);
  }

  @override
  Future<Device> fetchDevice() async {
    final body = await _api.get('/api/v1/me');
    return Device.fromJson(asMap(body['device']));
  }

  @override
  Future<List<ActivityEvent>> fetchEvents({int since = 0, int before = 0, int limit = 200}) async {
    final older = before > 0 ? '&before=$before' : '';
    final body = await _api.get('/api/v1/devices/$_deviceId/events?since=$since&limit=$limit$older');
    return (body['events'] as List? ?? const [])
        .map((e) => ActivityEvent.fromJson(asMap(e)))
        .toList();
  }

  @override
  Future<List<Shot>> fetchScreenshots({int beforeTs = 0, int limit = 120}) async {
    final older = beforeTs > 0 ? '&before=$beforeTs' : '';
    final body = await _api.get('/api/v1/devices/$_deviceId/screenshots?limit=$limit$older');
    return (body['screenshots'] as List? ?? const []).map((e) => Shot.fromJson(asMap(e))).toList();
  }

  @override
  Future<CommandResult> sendCommand(String type, {Map<String, dynamic>? args, String? code}) async {
    final body = await _api.post('/api/v1/devices/$_deviceId/command', {
      'type': type,
      if (args != null) 'args': args,
      if (code != null && code.isNotEmpty) 'code': code,
    });
    return CommandResult.fromJson(asMap(body['command']));
  }

  @override
  Future<CommandResult> unlock(String code, {String action = 'unlock'}) async {
    final body = await _api.post('/api/v1/devices/$_deviceId/unlock', {'code': code, 'action': action});
    return CommandResult.fromJson(asMap(body['command']));
  }

  @override
  Future<CommandResult> sendMessage({
    required String body,
    String title = 'Message from your phone',
    String kind = 'toast',
    bool allowReply = false,
  }) async {
    final response = await _api.post('/api/v1/devices/$_deviceId/message', {
      'body': body,
      'title': title,
      'kind': kind,
      'allow_reply': allowReply,
    });
    return CommandResult.fromJson(asMap(response['command']));
  }

  @override
  Future<bool> verifyCode(String code) async {
    final body = await _api.post('/api/v1/totp/check', {'code': code});
    return asBool(body['ok']);
  }

  @override
  Future<List<PhoneInfo>> fetchPhones() async {
    final body = await _api.get('/api/v1/devices/$_deviceId/phones');
    return (body['phones'] as List? ?? const [])
        .map((row) => PhoneInfo.fromJson(asMap(row)))
        .toList();
  }

  @override
  Future<void> removePhone(String id) async {
    await _api.delete('/api/v1/devices/$_deviceId/phones/$id');
  }

  @override
  Future<void> updateSettings({bool? require2fa, bool? protectPower}) async {
    await _api.patch('/api/v1/devices/$_deviceId', {
      'settings': {
        if (require2fa != null) 'require_2fa_unlock': require2fa,
        if (protectPower != null) 'protect_power': protectPower,
      }
    });
  }

  @override
  Future<void> renameDevice(String name) async {
    await _api.patch('/api/v1/devices/$_deviceId', {'name': name});
  }

  @override
  Future<String> fetchRelayVersion() async {
    final body = await _api.get('/api/v1/health');
    return asString(body['version']);
  }

  @override
  Future<void> removeDevice() async {
    await _api.delete('/api/v1/me');
  }

  @override
  void dispose() {
    _disposed = true;
    _retry?.cancel();
    _channel?.sink.close();
    _controller.close();
    _http.close();
  }
}

class DirectClient implements SentinelClient {
  @override
  final Session session;
  final http.Client _http = http.Client();
  late final _Http _api = _Http(session.baseUrl, session.token, _http);
  final StreamController<PushEvent> _controller = StreamController<PushEvent>.broadcast();
  Timer? _timer;
  bool _busy = false;
  bool _disposed = false;
  int _lastEventId = 0;

  DirectClient(this.session);

  @override
  String get baseUrl => session.baseUrl;

  @override
  bool get isDirect => true;

  @override
  Stream<PushEvent> get stream {
    _timer ??= Timer.periodic(const Duration(milliseconds: 2500), (_) => _cycle());
    _cycle();
    return _controller.stream;
  }

  Future<void> _cycle() async {
    if (_busy || _disposed) return;
    _busy = true;
    try {
      final device = await fetchDevice();
      _controller.add(PushEvent('state', device));
      final events = await fetchEvents(since: _lastEventId);
      if (events.isNotEmpty) {
        _lastEventId = events.last.id;
        _controller.add(PushEvent('events', events));
      }
    } catch (_) {
      _controller.add(const PushEvent('disconnected'));
    } finally {
      _busy = false;
    }
  }

  @override
  Future<Device> fetchDevice() async {
    final body = await _api.get('/local/status');
    return Device.fromJson(asMap(body['device']));
  }

  @override
  Future<List<ActivityEvent>> fetchEvents({int since = 0, int before = 0, int limit = 200}) async {
    final body = await _api.get('/local/events?since=$since&limit=$limit');
    return (body['events'] as List? ?? const []).map((e) => ActivityEvent.fromJson(asMap(e))).toList();
  }

  @override
  Future<List<Shot>> fetchScreenshots({int beforeTs = 0, int limit = 120}) async {
    final body = await _api.get('/local/screenshots');
    return (body['screenshots'] as List? ?? const []).map((e) => Shot.fromJson(asMap(e))).toList();
  }

  @override
  Future<CommandResult> sendCommand(String type, {Map<String, dynamic>? args, String? code}) async {
    final body = await _api.post('/local/command', {
      'type': type,
      if (args != null) 'args': args,
      if (code != null && code.isNotEmpty) 'code': code,
    });
    return CommandResult.fromJson(asMap(body['command']));
  }

  @override
  Future<CommandResult> unlock(String code, {String action = 'unlock'}) async {
    final body = await _api.post('/local/unlock', {'code': code, 'action': action});
    return CommandResult.fromJson(asMap(body['command']));
  }

  @override
  Future<CommandResult> sendMessage({
    required String body,
    String title = 'Message from your phone',
    String kind = 'toast',
    bool allowReply = false,
  }) =>
      sendCommand('message', args: {
        'text': body,
        'title': title,
        'kind': kind,
        'allow_reply': allowReply,
      });

  @override
  Future<bool> verifyCode(String code) async {
    final body = await _api.post('/local/totp/check', {'code': code});
    return asBool(body['ok']);
  }

  @override
  Future<List<PhoneInfo>> fetchPhones() async => const [];

  @override
  Future<void> removePhone(String id) async =>
      throw const ApiError(400, 'unsupported_direct_mode');

  @override
  Future<void> updateSettings({bool? require2fa, bool? protectPower}) async {
    throw const ApiError(400, 'unsupported_direct_mode');
  }

  @override
  Future<void> renameDevice(String name) async {
    throw const ApiError(400, 'unsupported_direct_mode');
  }

  @override
  Future<String> fetchRelayVersion() async => 'direct';

  @override
  Future<void> removeDevice() async {}

  @override
  void dispose() {
    _disposed = true;
    _timer?.cancel();
    _controller.close();
    _http.close();
  }
}

SentinelClient createClient(Session session) =>
    session.isDirect ? DirectClient(session) : RelayClient(session);

class Pairer {
  static Future<Session> pairRelay({
    required String baseUrl,
    required String pairCode,
    String? requestId,
    String mobileName = '',
    String mobileUid = '',
  }) async {
    final name = mobileName.trim().isEmpty ? sentinelDeviceName : mobileName.trim();
    final base = normalizeBaseUrl(baseUrl);
    if (base.isEmpty) throw const ApiError(400, 'bad_url');
    final uid = mobileUid.isEmpty ? newMobileUid() : mobileUid;
    final client = http.Client();
    try {
      var rid = requestId ?? '';
      var label = Uri.parse(base).host;
      if (rid.isEmpty) {
        final lookup = await _post(client, base, '/api/v1/pair/lookup', {'pair_code': pairCode});
        rid = asString(lookup['request_id']);
        final server = asMap(lookup['server']);
        if (asString(server['name']).isNotEmpty) label = asString(server['name']);
        if (asString(lookup['device_name']).isNotEmpty) label = asString(lookup['device_name']);
      }
      final body = await _post(client, base, '/api/v1/pair/approve', {
        'request_id': rid,
        'pair_code': pairCode,
        'mobile_name': name,
        'mobile_uid': uid,
      });
      final device = Device.fromJson(asMap(body['device']));
      return Session(
        baseUrl: base,
        mode: 'relay',
        token: asString(body['mobile_token']),
        totpSecret: asString(body['totp_secret'], device.totpSecret ?? ''),
        deviceId: device.id,
        deviceName: device.name,
        label: label.isEmpty ? device.name : label,
        pairedAt: DateTime.now().millisecondsSinceEpoch,
        uid: uid,
      );
    } finally {
      client.close();
    }
  }

  static Future<Session> pairDirect({
    required String baseUrl,
    required String pairCode,
    String mobileName = '',
  }) async {
    final base = normalizeBaseUrl(baseUrl);
    if (base.isEmpty) throw const ApiError(400, 'bad_url');
    final name = mobileName.trim().isEmpty ? sentinelDeviceName : mobileName.trim();
    final client = http.Client();
    try {
      final body = await _post(client, base, '/local/pair', {'code': pairCode, 'mobile_name': name});
      return Session(
        baseUrl: base,
        mode: 'direct',
        token: asString(body['token']),
        totpSecret: asString(body['totp_secret']),
        deviceId: asString(body['device_id']),
        deviceName: asString(body['device_name'], 'Windows PC'),
        label: 'Direct · ${asString(body['device_name'], 'PC')}',
        pairedAt: DateTime.now().millisecondsSinceEpoch,
      );
    } finally {
      client.close();
    }
  }

  static Future<Map<String, dynamic>> testConnection(String baseUrl) async {
    final base = normalizeBaseUrl(baseUrl);
    final client = http.Client();
    try {
      final response = await client
          .get(Uri.parse('$base/api/v1/health'))
          .timeout(const Duration(seconds: 8));
      if (response.statusCode >= 400) throw ApiError(response.statusCode, 'http_${response.statusCode}');
      final decoded = jsonDecode(response.body);
      return decoded is Map<String, dynamic> ? decoded : <String, dynamic>{};
    } finally {
      client.close();
    }
  }

  static Future<Map<String, dynamic>> _post(
    http.Client client,
    String base,
    String path,
    Map<String, dynamic> body,
  ) async {
    final response = await client
        .post(Uri.parse('$base$path'),
            headers: {
              'Content-Type': 'application/json',
              'X-App-Version': sentinelAppVersion,
              'User-Agent': 'SentinelPhone/$sentinelAppVersion',
            },
            body: jsonEncode(body))
        .timeout(const Duration(seconds: 15));
    Map<String, dynamic>? decoded;
    try {
      final raw = jsonDecode(response.body);
      if (raw is Map<String, dynamic>) decoded = raw;
    } catch (_) {
      decoded = null;
    }
    if (response.statusCode >= 400) {
      throw ApiError(response.statusCode, asString(decoded?['error'], 'http_${response.statusCode}'));
    }
    return decoded ?? <String, dynamic>{};
  }
}
