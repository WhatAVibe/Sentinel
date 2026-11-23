import 'dart:convert';
import 'dart:typed_data';

import 'package:cryptography/cryptography.dart';

class CryptoException implements Exception {
  final String code;

  const CryptoException(this.code);

  @override
  String toString() => code;
}

class CryptoBox {
  static const nonceLength = 12;
  static const macLength = 16;
  static const keyLength = 32;
  static const _aadPrefix = 'sentinel:v2:';

  static const event = 'event';
  static const message = 'message';
  static const shot = 'shot';

  static const _kinds = {event, message, shot};

  final SecretKey _key;
  final AesGcm _algorithm = AesGcm.with256bits();

  CryptoBox._(this._key);

  factory CryptoBox.fromBase64(String value) {
    final raw = value.trim();
    if (raw.isEmpty) throw const CryptoException('invalid_key');
    late List<int> bytes;
    try {
      bytes = base64Decode(raw);
    } catch (_) {
      throw const CryptoException('invalid_key');
    }
    if (bytes.length != keyLength) throw const CryptoException('invalid_key');
    return CryptoBox._(SecretKey(bytes));
  }

  static bool isValidKey(String value) {
    try {
      CryptoBox.fromBase64(value);
      return true;
    } catch (_) {
      return false;
    }
  }

  List<int> _aad(String kind) {
    if (!_kinds.contains(kind)) throw const CryptoException('invalid_kind');
    return utf8.encode('$_aadPrefix$kind');
  }

  Future<String> encrypt(String kind, List<int> plaintext) async {
    final box = await _algorithm.encrypt(
      plaintext,
      secretKey: _key,
      nonce: _algorithm.newNonce(),
      aad: _aad(kind),
    );
    return base64Encode(box.concatenation());
  }

  Future<Uint8List> decryptBytes(String kind, String envelope) async {
    final aad = _aad(kind);
    Uint8List bytes;
    try {
      bytes = base64Decode(envelope.trim());
    } catch (_) {
      throw const CryptoException('bad_envelope');
    }
    return _open(bytes, aad);
  }

  Future<String> decrypt(String kind, String envelope) async =>
      utf8.decode(await decryptBytes(kind, envelope));

  Future<Uint8List> _open(Uint8List envelope, List<int> aad) async {
    if (envelope.length < nonceLength + macLength) {
      throw const CryptoException('bad_envelope');
    }
    try {
      final box = SecretBox.fromConcatenation(
        envelope,
        nonceLength: nonceLength,
        macLength: macLength,
      );
      final clear = await _algorithm.decrypt(box, secretKey: _key, aad: aad);
      return Uint8List.fromList(clear);
    } catch (_) {
      throw const CryptoException('decrypt_failed');
    }
  }
}
