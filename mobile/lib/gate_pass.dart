import 'dart:convert';
import 'dart:typed_data';

import 'package:crypto/crypto.dart';

/// The rotating gate pass: same rule as the server (Tas.Application.Gate.GatePassCode).
/// QR = "YS1:{passId}:{appointmentNo}:{code}", code = 8 digits from HMAC-SHA256 over the 30-second step.
class GatePass {
  const GatePass({required this.passId, required this.secret, this.prefix = 'YS1', this.stepSeconds = 30, this.digits = 8});

  final int passId;
  final List<int> secret;
  final String prefix;
  final int stepSeconds;
  final int digits;

  factory GatePass.fromJson(Map<String, dynamic> json) => GatePass(
        passId: (json['passId'] as num).toInt(),
        secret: base64Decode(json['secret'] as String),
        prefix: (json['prefix'] as String?) ?? 'YS1',
        stepSeconds: (json['stepSeconds'] as num?)?.toInt() ?? 30,
        digits: (json['digits'] as num?)?.toInt() ?? 8,
      );

  Map<String, dynamic> toJson() => {
        'passId': passId,
        'secret': base64Encode(secret),
        'prefix': prefix,
        'stepSeconds': stepSeconds,
        'digits': digits,
      };

  int step(DateTime now) => now.toUtc().millisecondsSinceEpoch ~/ 1000 ~/ stepSeconds;

  /// Seconds until the code changes.
  int secondsLeft(DateTime now) => stepSeconds - (now.toUtc().millisecondsSinceEpoch ~/ 1000) % stepSeconds;

  String code(int step) => gatePassCode(secret, step, digits);

  String payload(String appointmentNo, DateTime now) => '$prefix:$passId:$appointmentNo:${code(step(now))}';
}

String gatePassCode(List<int> secret, int step, [int digits = 8]) {
  final counter = ByteData(8)..setInt64(0, step, Endian.big);
  final hash = Hmac(sha256, secret).convert(counter.buffer.asUint8List()).bytes;
  final offset = hash[hash.length - 1] & 0x0f;
  final value = ((hash[offset] & 0x7f) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
  var mod = 1;
  for (var i = 0; i < digits; i++) {
    mod *= 10;
  }
  return (value % mod).toString().padLeft(digits, '0');
}
