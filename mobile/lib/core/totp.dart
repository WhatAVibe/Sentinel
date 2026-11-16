import 'dart:typed_data';

import 'package:crypto/crypto.dart';

const _alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567';

Uint8List _base32Decode(String input) {
  final clean = input.toUpperCase().replaceAll(RegExp(r'[^A-Z2-7]'), '');
  final out = <int>[];
  var buffer = 0;
  var bits = 0;
  for (final code in clean.codeUnits) {
    final value = _alphabet.indexOf(String.fromCharCode(code));
    if (value < 0) continue;
    buffer = (buffer << 5) | value;
    bits += 5;
    if (bits >= 8) {
      bits -= 8;
      out.add((buffer >> bits) & 0xFF);
    }
  }
  return Uint8List.fromList(out);
}

String totpCode(String secret, {int step = 30, int digits = 6, DateTime? at}) {
  final key = _base32Decode(secret);
  if (key.isEmpty) return '';
  final seconds = (at ?? DateTime.now()).millisecondsSinceEpoch ~/ 1000;
  final counter = seconds ~/ step;
  final data = ByteData(8)..setUint64(0, counter);
  final digest = Hmac(sha1, key).convert(data.buffer.asUint8List()).bytes;
  final offset = digest.last & 0x0F;
  final value = ((digest[offset] & 0x7F) << 24) |
      ((digest[offset + 1] & 0xFF) << 16) |
      ((digest[offset + 2] & 0xFF) << 8) |
      (digest[offset + 3] & 0xFF);
  var mod = 1;
  for (var i = 0; i < digits; i++) {
    mod *= 10;
  }
  return (value % mod).toString().padLeft(digits, '0');
}

int totpSecondsRemaining({int step = 30}) {
  final seconds = DateTime.now().millisecondsSinceEpoch ~/ 1000;
  return step - (seconds % step);
}

String formatCode(String code) {
  if (code.length != 6) return code;
  return '${code.substring(0, 3)} ${code.substring(3)}';
}
