import 'dart:convert';
import 'dart:math';

import 'package:crypto/crypto.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:shared_preferences/shared_preferences.dart';

import '../models.dart';

class SessionStore {
  static const _sessionKey = 'sentinel_session_v1';
  static const _sessionsKey = 'sentinel_sessions_v2';
  static const _activeKey = 'sentinel_active_device_v2';
  static const _pinKey = 'sentinel_pin_v1';
  static const _biometricKey = 'sentinel_biometric_enabled';

  final FlutterSecureStorage _secure = const FlutterSecureStorage();
  final Random _random = Random.secure();

  Future<Session?> loadSession() async {
    try {
      final raw = await _secure.read(key: _sessionKey);
      if (raw == null || raw.isEmpty) return null;
      final decoded = jsonDecode(raw);
      if (decoded is! Map<String, dynamic>) return null;
      final session = Session.fromJson(decoded);
      if (session.baseUrl.isEmpty || session.token.isEmpty) return null;
      return session;
    } catch (_) {
      return null;
    }
  }

  Future<void> saveSession(Session session) =>
      _secure.write(key: _sessionKey, value: jsonEncode(session.toJson()));

  Future<void> clearSession() => _secure.delete(key: _sessionKey);

  Future<List<Session>> loadSessions() async {
    try {
      final raw = await _secure.read(key: _sessionsKey);
      if (raw != null && raw.isNotEmpty) {
        final decoded = jsonDecode(raw);
        if (decoded is List) {
          return decoded
              .whereType<Map>()
              .map((e) => Session.fromJson(Map<String, dynamic>.from(e)))
              .where((s) => s.deviceId.isNotEmpty && s.token.isNotEmpty)
              .toList();
        }
      }
      final legacy = await loadSession();
      if (legacy != null) {
        await saveSessions([legacy]);
        await saveActiveId(legacy.deviceId);
        return [legacy];
      }
      return [];
    } catch (_) {
      return [];
    }
  }

  Future<void> saveSessions(List<Session> sessions) => _secure.write(
        key: _sessionsKey,
        value: jsonEncode(sessions.map((s) => s.toJson()).toList()),
      );

  Future<String?> loadActiveId() => _secure.read(key: _activeKey);

  Future<void> saveActiveId(String? deviceId) async {
    if (deviceId == null || deviceId.isEmpty) {
      await _secure.delete(key: _activeKey);
    } else {
      await _secure.write(key: _activeKey, value: deviceId);
    }
  }

  Future<void> clearSessions() async {
    await _secure.delete(key: _sessionsKey);
    await _secure.delete(key: _activeKey);
    await _secure.delete(key: _sessionKey);
  }

  Future<bool> biometricEnabled() async {
    final prefs = await SharedPreferences.getInstance();
    return prefs.getBool(_biometricKey) ?? true;
  }

  Future<void> setBiometricEnabled(bool value) async {
    final prefs = await SharedPreferences.getInstance();
    await prefs.setBool(_biometricKey, value);
  }

  Future<bool> hasPin() async => (await _secure.read(key: _pinKey)) != null;

  Future<void> savePin(String pin) async {
    final salt = List<int>.generate(16, (_) => _random.nextInt(256));
    final hash = sha256.convert([...salt, ...utf8.encode(pin)]).toString();
    await _secure.write(
      key: _pinKey,
      value: jsonEncode({'salt': base64Encode(salt), 'hash': hash}),
    );
  }

  Future<bool> verifyPin(String pin) async {
    try {
      final raw = await _secure.read(key: _pinKey);
      if (raw == null) return false;
      final decoded = jsonDecode(raw);
      if (decoded is! Map<String, dynamic>) return false;
      final salt = base64Decode(asString(decoded['salt']));
      final hash = sha256.convert([...salt, ...utf8.encode(pin)]).toString();
      return hash == asString(decoded['hash']);
    } catch (_) {
      return false;
    }
  }

  Future<void> clearPin() => _secure.delete(key: _pinKey);

  Future<bool> tutorialDone() async {
    final prefs = await SharedPreferences.getInstance();
    return prefs.getBool('sentinel_tutorial_done') ?? false;
  }

  Future<void> setTutorialDone(bool value) async {
    final prefs = await SharedPreferences.getInstance();
    await prefs.setBool('sentinel_tutorial_done', value);
  }

  Future<bool> widgetAllowWithoutUnlock() async {
    final prefs = await SharedPreferences.getInstance();
    return prefs.getBool('widget_allow_without_unlock') ?? false;
  }

  Future<void> setWidgetAllowWithoutUnlock(bool value) async {
    final prefs = await SharedPreferences.getInstance();
    await prefs.setBool('widget_allow_without_unlock', value);
  }

  Future<bool> widgetShowCode() async {
    final prefs = await SharedPreferences.getInstance();
    return prefs.getBool('widget_show_code') ?? true;
  }

  Future<void> setWidgetShowCode(bool value) async {
    final prefs = await SharedPreferences.getInstance();
    await prefs.setBool('widget_show_code', value);
  }

  Future<bool> notificationEnabled(String category) async {
    final prefs = await SharedPreferences.getInstance();
    return prefs.getBool('notify_$category') ?? true;
  }

  Future<void> setNotificationEnabled(String category, bool value) async {
    final prefs = await SharedPreferences.getInstance();
    await prefs.setBool('notify_$category', value);
  }
}
