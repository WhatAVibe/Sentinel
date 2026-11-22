import 'package:flutter/services.dart';

class SentinelWidgets {
  static const MethodChannel _channel = MethodChannel('sentinel/widgets');

  static Future<void> configure({
    required String baseUrl,
    required String token,
    required String deviceId,
    required String name,
    required String totp,
    required bool allowWithoutUnlock,
    required bool showCode,
  }) async {
    try {
      await _channel.invokeMethod('configure', {
        'baseUrl': baseUrl,
        'token': token,
        'deviceId': deviceId,
        'name': name,
        'totp': totp,
        'allowWithoutUnlock': allowWithoutUnlock,
        'showCode': showCode,
      });
    } catch (_) {
      // widgets are Android-only
    }
  }

  static Future<void> clear() async {
    try {
      await _channel.invokeMethod('clear');
    } catch (_) {}
  }

  static Future<void> refresh() async {
    try {
      await _channel.invokeMethod('refresh');
    } catch (_) {}
  }

  static Future<bool> pin(String kind) async {
    try {
      return await _channel.invokeMethod<bool>('pin', {'kind': kind}) ?? false;
    } catch (_) {
      return false;
    }
  }
}
