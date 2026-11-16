import 'package:flutter/foundation.dart';

class DiagEntry {
  final DateTime time;
  final String tag;
  final String message;
  final bool error;

  const DiagEntry(this.time, this.tag, this.message, this.error);

  String get timeText {
    final h = time.hour.toString().padLeft(2, '0');
    final m = time.minute.toString().padLeft(2, '0');
    final s = time.second.toString().padLeft(2, '0');
    return '$h:$m:$s';
  }
}

class DiagLog {
  DiagLog._();

  static final DiagLog instance = DiagLog._();
  static const int _capacity = 500;

  final List<DiagEntry> _entries = [];
  final ValueNotifier<int> revision = ValueNotifier<int>(0);

  void add(String tag, String message, {bool error = false}) {
    _entries.add(DiagEntry(DateTime.now(), tag, _redact(message), error));
    if (_entries.length > _capacity) _entries.removeRange(0, _entries.length - _capacity);
    revision.value++;
  }

  List<DiagEntry> snapshot({int limit = 200}) {
    final start = _entries.length > limit ? _entries.length - limit : 0;
    return _entries.sublist(start).reversed.toList();
  }

  void clear() {
    _entries.clear();
    revision.value++;
  }

  static String _redact(String value) {
    var result = value;
    final tokenIndex = result.toLowerCase().indexOf('token=');
    while (tokenIndex >= 0) {
      final end = result.indexOf('&', tokenIndex);
      final suffix = end >= 0 ? result.substring(end) : '';
      result = '${result.substring(0, tokenIndex)}token=***$suffix';
      final next = result.toLowerCase().indexOf('token=', tokenIndex + 8);
      if (next <= tokenIndex) break;
    }
    return result;
  }
}
