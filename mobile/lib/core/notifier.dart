import 'package:flutter_local_notifications/flutter_local_notifications.dart';

class Notifier {
  static final FlutterLocalNotificationsPlugin _plugin = FlutterLocalNotificationsPlugin();
  static bool _ready = false;
  static DateTime _last = DateTime.fromMillisecondsSinceEpoch(0);
  static String _lastTitle = '';

  static Future<void> init() async {
    try {
      const settings = InitializationSettings(
        android: AndroidInitializationSettings('@mipmap/ic_launcher'),
      );
      await _plugin.initialize(settings);
      final android = _plugin.resolvePlatformSpecificImplementation<
          AndroidFlutterLocalNotificationsPlugin>();
      await android?.requestNotificationsPermission();
      _ready = true;
    } catch (_) {
      _ready = false;
    }
  }

  static Future<void> show(String title, String body, {bool hideDetails = false}) async {
    if (!_ready) return;
    final now = DateTime.now();
    if (title == _lastTitle && now.difference(_last) < const Duration(seconds: 8)) return;
    _last = now;
    _lastTitle = title;
    final safeTitle = hideDetails ? 'Sentinel' : title;
    final safeBody = hideDetails
        ? 'Open Sentinel to view this event.'
        : (body.length > 900 ? '${body.substring(0, 897)}...' : body);
    try {
      final details = NotificationDetails(
        android: AndroidNotificationDetails(
          'sentinel_alerts',
          'Sentinel alerts',
          channelDescription: 'Away mode, messages and security events',
          importance: Importance.high,
          priority: Priority.high,
          styleInformation: BigTextStyleInformation(safeBody, contentTitle: safeTitle),
        ),
      );
      await _plugin.show(
        now.millisecondsSinceEpoch ~/ 1000 % 100000,
        safeTitle,
        safeBody,
        details,
      );
    } catch (_) {
      // notifications are best-effort
    }
  }
}
