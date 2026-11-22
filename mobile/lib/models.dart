import 'dart:math';

import 'package:flutter/services.dart';

int asInt(dynamic value, [int fallback = 0]) {
  if (value is int) return value;
  if (value is num) return value.toInt();
  return int.tryParse('${value ?? ''}') ?? fallback;
}

double asDouble(dynamic value, [double fallback = 0]) {
  if (value is double) return value;
  if (value is num) return value.toDouble();
  return double.tryParse('${value ?? ''}') ?? fallback;
}

bool asBool(dynamic value, [bool fallback = false]) {
  if (value is bool) return value;
  if (value is num) return value != 0;
  if (value is String) return value.toLowerCase() == 'true' || value == '1';
  return fallback;
}

String asString(dynamic value, [String fallback = '']) => value == null ? fallback : '$value';

Map<String, dynamic> asMap(dynamic value) =>
    value is Map<String, dynamic> ? value : <String, dynamic>{};

class Metrics {
  final double cpuPercent;
  final int memUsedMb;
  final int memTotalMb;
  final int uptimeSeconds;
  final String user;
  final String hostname;

  const Metrics({
    this.cpuPercent = 0,
    this.memUsedMb = 0,
    this.memTotalMb = 0,
    this.uptimeSeconds = 0,
    this.user = '',
    this.hostname = '',
  });

  double get memPercent => memTotalMb == 0 ? 0 : memUsedMb * 100 / memTotalMb;
  String get memText => memTotalMb == 0
      ? '—'
      : '${(memUsedMb / 1024).toStringAsFixed(1)} / ${(memTotalMb / 1024).toStringAsFixed(1)} GB';

  String get uptimeText {
    if (uptimeSeconds <= 0) return '—';
    final d = Duration(seconds: uptimeSeconds);
    return d.inDays >= 1 ? '${d.inDays}d ${d.inHours % 24}h' : '${d.inHours}h ${d.inMinutes % 60}m';
  }

  factory Metrics.fromJson(Map<String, dynamic> json) => Metrics(
        cpuPercent: asDouble(json['cpu_percent']),
        memUsedMb: asInt(json['mem_used_mb']),
        memTotalMb: asInt(json['mem_total_mb']),
        uptimeSeconds: asInt(json['uptime_seconds']),
        user: asString(json['user']),
        hostname: asString(json['hostname']),
      );
}

class DeviceState {
  final int monitors;
  final String session;
  final bool screensaver;
  final int idleSeconds;
  final bool phonePresent;
  final bool secureLock;

  const DeviceState({
    this.monitors = 0,
    this.session = 'unknown',
    this.screensaver = false,
    this.idleSeconds = 0,
    this.phonePresent = false,
    this.secureLock = false,
  });

  factory DeviceState.fromJson(Map<String, dynamic> json) => DeviceState(
        monitors: asInt(json['monitors']),
        session: asString(json['session'], 'unknown'),
        screensaver: asBool(json['screensaver']),
        idleSeconds: asInt(json['idle_seconds']),
        phonePresent: asBool(json['phone_present']),
        secureLock: asBool(json['secure_lock']),
      );
}

class Device {
  final String id;
  final String name;
  final String platform;
  final bool online;
  final bool away;
  final bool locked;
  final int lastSeen;
  final Metrics metrics;
  final DeviceState state;
  final bool require2fa;
  final bool protectPower;
  final String? totpSecret;
  final String agentVersion;

  const Device({
    required this.id,
    required this.name,
    this.platform = 'windows',
    this.online = false,
    this.away = false,
    this.locked = false,
    this.lastSeen = 0,
    this.metrics = const Metrics(),
    this.state = const DeviceState(),
    this.require2fa = true,
    this.protectPower = false,
    this.totpSecret,
    this.agentVersion = '',
  });

  factory Device.fromJson(Map<String, dynamic> json) {
    final settings = asMap(json['settings']);
    return Device(
      id: asString(json['id']),
      name: asString(json['name'], 'Windows PC'),
      platform: asString(json['platform'], 'windows'),
      online: asBool(json['online']),
      away: asBool(json['away']),
      locked: asBool(json['locked']),
      lastSeen: asInt(json['last_seen']),
      metrics: Metrics.fromJson(asMap(json['metrics'])),
      state: DeviceState.fromJson(asMap(json['state'])),
      require2fa: settings['require_2fa_unlock'] == null ? true : asBool(settings['require_2fa_unlock']),
      protectPower: asBool(settings['protect_power']),
      totpSecret: json['totp_secret'] == null ? null : asString(json['totp_secret']),
      agentVersion: asString(json['agent_version']),
    );
  }

  DateTime? get lastSeenAt => lastSeen == 0 ? null : DateTime.fromMillisecondsSinceEpoch(lastSeen);

  String get lastSeenText {
    final at = lastSeenAt;
    if (at == null) return 'never';
    final diff = DateTime.now().difference(at);
    if (diff.inSeconds < 20) return 'just now';
    if (diff.inMinutes < 60) return '${diff.inMinutes} min ago';
    if (diff.inHours < 24) return '${diff.inHours}h ago';
    return '${diff.inDays}d ago';
  }
}

class ActivityEvent {
  final int id;
  final int ts;
  final String category;
  final String severity;
  final String title;
  final Map<String, dynamic>? detail;

  const ActivityEvent({
    required this.id,
    required this.ts,
    required this.category,
    required this.severity,
    required this.title,
    this.detail,
  });

  DateTime get time => DateTime.fromMillisecondsSinceEpoch(ts);

  String get detailText {
    final d = detail;
    if (d == null || d.isEmpty) return '';
    return d.entries.map((e) => '${e.key}: ${e.value}').join('  ·  ');
  }

  factory ActivityEvent.fromJson(Map<String, dynamic> json) {
    final raw = json['detail'];
    Map<String, dynamic>? detail;
    if (raw is Map<String, dynamic>) {
      detail = raw;
    } else if (raw is String && raw.isNotEmpty) {
      detail = {'value': raw};
    }
    return ActivityEvent(
      id: asInt(json['id']),
      ts: asInt(json['ts']),
      category: asString(json['category'], 'system'),
      severity: asString(json['severity'], 'info'),
      title: asString(json['title']),
      detail: detail,
    );
  }
}

class Shot {
  final String id;
  final int ts;
  final int monitor;
  final String monitorName;
  final int width;
  final int height;
  final String trigger;
  final String url;

  const Shot({
    required this.id,
    required this.ts,
    this.monitor = 0,
    this.monitorName = '',
    this.width = 0,
    this.height = 0,
    this.trigger = 'manual',
    this.url = '',
  });

  DateTime get time => DateTime.fromMillisecondsSinceEpoch(ts);
  String get caption => monitorName.isNotEmpty ? monitorName : 'Display $monitor';

  factory Shot.fromJson(Map<String, dynamic> json) => Shot(
        id: asString(json['id']),
        ts: asInt(json['ts']),
        monitor: asInt(json['monitor']),
        monitorName: asString(json['monitor_name']),
        width: asInt(json['width']),
        height: asInt(json['height']),
        trigger: asString(json['trigger'], 'manual'),
        url: asString(json['url']),
      );
}

class CommandResult {
  final bool ok;
  final String? error;
  final String status;
  final Map<String, dynamic>? data;

  const CommandResult({required this.ok, this.error, this.status = 'done', this.data});

  factory CommandResult.fromJson(Map<String, dynamic> json) {
    final result = asMap(json['result']);
    return CommandResult(
      ok: json['ok'] == null ? result['ok'] != false : asBool(json['ok']),
      error: (json['error'] ?? result['error']) as String?,
      status: asString(json['status'], 'done'),
      data: result['data'] is Map<String, dynamic> ? result['data'] as Map<String, dynamic> : null,
    );
  }
}

class PushEvent {
  final String type;
  final dynamic data;

  const PushEvent(this.type, [this.data]);
}

class Session {
  final String baseUrl;
  final String mode;
  final String token;
  final String totpSecret;
  final String deviceId;
  final String deviceName;
  final String label;
  final int pairedAt;
  final String uid;
  final String localName;

  const Session({
    required this.baseUrl,
    required this.mode,
    required this.token,
    required this.totpSecret,
    required this.deviceId,
    required this.deviceName,
    required this.label,
    required this.pairedAt,
    this.uid = '',
    this.localName = '',
  });

  bool get isDirect => mode == 'direct';

  String get displayName => localName.isNotEmpty ? localName : deviceName;

  Map<String, dynamic> toJson() => {
        'base_url': baseUrl,
        'mode': mode,
        'token': token,
        'totp_secret': totpSecret,
        'device_id': deviceId,
        'device_name': deviceName,
        'label': label,
        'paired_at': pairedAt,
        'uid': uid,
        'local_name': localName,
      };

  factory Session.fromJson(Map<String, dynamic> json) => Session(
        baseUrl: asString(json['base_url']),
        mode: asString(json['mode'], 'relay'),
        token: asString(json['token']),
        totpSecret: asString(json['totp_secret']),
        deviceId: asString(json['device_id']),
        deviceName: asString(json['device_name'], 'Windows PC'),
        label: asString(json['label'], 'Sentinel'),
        pairedAt: asInt(json['paired_at']),
        uid: asString(json['uid']),
        localName: asString(json['local_name']),
      );

  Session withBaseUrl(String value) => Session(
        baseUrl: value,
        mode: mode,
        token: token,
        totpSecret: totpSecret,
        deviceId: deviceId,
        deviceName: deviceName,
        label: label,
        pairedAt: pairedAt,
        uid: uid,
        localName: localName,
      );

  Session withUid(String value) => Session(
        baseUrl: baseUrl,
        mode: mode,
        token: token,
        totpSecret: totpSecret,
        deviceId: deviceId,
        deviceName: deviceName,
        label: label,
        pairedAt: pairedAt,
        uid: value,
        localName: localName,
      );

  Session withLocalName(String value) => Session(
        baseUrl: baseUrl,
        mode: mode,
        token: token,
        totpSecret: totpSecret,
        deviceId: deviceId,
        deviceName: deviceName,
        label: label,
        pairedAt: pairedAt,
        uid: uid,
        localName: value,
      );
}

class PhoneInfo {
  final String id;
  final String name;
  final int createdAt;
  final int? lastSeen;
  final bool current;

  const PhoneInfo({
    required this.id,
    required this.name,
    required this.createdAt,
    this.lastSeen,
    this.current = false,
  });

  DateTime? get created => createdAt == 0 ? null : DateTime.fromMillisecondsSinceEpoch(createdAt);
  DateTime? get seen => lastSeen == null ? null : DateTime.fromMillisecondsSinceEpoch(lastSeen!);

  factory PhoneInfo.fromJson(Map<String, dynamic> json) => PhoneInfo(
        id: asString(json['id']),
        name: asString(json['name'], 'Phone'),
        createdAt: asInt(json['created_at']),
        lastSeen: json['last_seen'] == null ? null : asInt(json['last_seen']),
        current: asBool(json['current']),
      );
}

String newMobileUid() {
  final random = Random.secure();
  final bytes = List<int>.generate(12, (_) => random.nextInt(256));
  return bytes.map((value) => value.toRadixString(16).padLeft(2, '0')).join();
}

const MethodChannel _deviceChannel = MethodChannel('sentinel/device');

/// Human-readable model of this phone, reported to the relay so linked
/// devices show "Samsung SM-S948U1" instead of a generic "Sentinel".
String sentinelDeviceName = '';

Future<void> loadDeviceName() async {
  try {
    final name = await _deviceChannel.invokeMethod<String>('name');
    if (name != null && name.trim().isNotEmpty) sentinelDeviceName = name.trim();
  } catch (_) {
    // Not Android (or test environment); fall through.
  }
  if (sentinelDeviceName.isEmpty) sentinelDeviceName = 'Android phone';
}

class ApiError implements Exception {  final int status;
  final String code;

  const ApiError(this.status, this.code);

  String get message {
    switch (code) {
      case 'bad_code':
        return 'That code is not valid right now.';
      case 'too_many_attempts':
        return 'Too many attempts. Wait a minute.';
      case 'invalid_mobile_token':
      case 'unauthorized':
        return 'This phone is no longer authorized. Pair again.';
      case 'phone_not_present':
        return 'The PC requires an ADB presence link — enable Direct link and connect this phone.';
      case 'capture_busy':
        return 'A capture is already running.';
      case 'unsupported_command':
        return 'Update the PC agent to use this feature.';
      case 'pending_confirmation':
        return 'Confirm this device on the PC with the 6-digit code shown here.';
      case 'primary_device':
        return 'This is the primary device. Unset primary on the PC first.';
      case 'no_linked_device':
        return 'Link a device first: 2FA actions are disabled without one.';
      case 'clipboard_failed':
      case 'missing_text':
        return 'The PC could not replace its clipboard.';
      case 'rate_limited':
        return 'Slow down and try again.';
      default:
        return 'Request failed ($code).';
    }
  }

  @override
  String toString() => code;
}

String friendlyError(String? code) {
  if (code == null || code.isEmpty) return 'Something went wrong.';
  return ApiError(0, code).message;
}
