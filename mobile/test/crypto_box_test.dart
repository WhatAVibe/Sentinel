import 'dart:convert';

import 'package:flutter_test/flutter_test.dart';
import 'package:sentinel/core/crypto_box.dart';

void main() {
  final key = base64Encode(List<int>.generate(32, (i) => i));

  test('sealed envelope is nonce + ciphertext + tag', () async {
    final box = CryptoBox.fromBase64(key);
    final sealed = await box.encrypt(CryptoBox.event, utf8.encode('hello'));
    final raw = base64Decode(sealed);
    expect(raw.length, CryptoBox.nonceLength + 5 + CryptoBox.macLength);
  });

  test('round trip per kind', () async {
    final box = CryptoBox.fromBase64(key);
    for (final kind in [CryptoBox.event, CryptoBox.message, CryptoBox.shot]) {
      final sealed = await box.encrypt(kind, utf8.encode('payload-$kind'));
      expect(await box.decrypt(kind, sealed), 'payload-$kind');
    }
  });

  test('wrong kind fails closed', () async {
    final box = CryptoBox.fromBase64(key);
    final sealed = await box.encrypt(CryptoBox.event, utf8.encode('secret'));
    await expectLater(box.decrypt(CryptoBox.message, sealed), throwsA(isA<CryptoException>()));
  });

  test('bad base64 and bad tag fail closed', () async {
    final box = CryptoBox.fromBase64(key);
    await expectLater(box.decrypt(CryptoBox.event, 'not base64!'), throwsA(isA<CryptoException>()));
    final sealed = await box.encrypt(CryptoBox.event, utf8.encode('secret'));
    final raw = base64Decode(sealed);
    raw[raw.length - 1] ^= 0xff;
    await expectLater(
      box.decrypt(CryptoBox.event, base64Encode(raw)),
      throwsA(isA<CryptoException>()),
    );
  });

  test('rejects keys that are not 32 bytes', () {
    expect(CryptoBox.isValidKey(base64Encode(List<int>.filled(16, 0))), isFalse);
    expect(CryptoBox.isValidKey('***'), isFalse);
    expect(CryptoBox.isValidKey(key), isTrue);
  });
}
