import 'package:flutter/services.dart';

class WatchBridge {
  static const MethodChannel _channel = MethodChannel('sentinel/watch');

  static Future<bool> pair({
    required String baseUrl,
    required String deviceId,
    required String deviceName,
    required String token,
    required String secret,
    required String dataKey,
    required String name,
  }) async {
    try {
      return await _channel.invokeMethod<bool>('pair', {
            'url': baseUrl,
            'deviceId': deviceId,
            'deviceName': deviceName,
            'token': token,
            'secret': secret,
            'dataKey': dataKey,
            'name': name,
          }) ??
          false;
    } catch (_) {
      return false;
    }
  }
}
