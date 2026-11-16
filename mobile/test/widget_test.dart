import 'package:flutter_test/flutter_test.dart';
import 'package:sentinel/core/totp.dart';

void main() {
  test('TOTP matches RFC 6238 SHA-1 vectors', () {
    const secret = 'GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ';
    expect(
      totpCode(secret, digits: 8, at: DateTime.fromMillisecondsSinceEpoch(59000)),
      '94287082',
    );
    expect(
      totpCode(secret, digits: 8, at: DateTime.fromMillisecondsSinceEpoch(1234567890000)),
      '89005924',
    );
    expect(
      totpCode(secret, digits: 8, at: DateTime.fromMillisecondsSinceEpoch(20000000000000)),
      '65353130',
    );
  });

  test('TOTP formatting groups digits', () {
    expect(formatCode('123456'), '123 456');
  });
}
