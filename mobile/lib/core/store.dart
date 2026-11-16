import 'dart:convert';
import 'dart:math';

import 'package:crypto/crypto.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:shared_preferences/shared_preferences.dart';

import '../models.dart';

class SessionStore {
  static const _sessionKey = 'sentinel_session_v1';
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
}
