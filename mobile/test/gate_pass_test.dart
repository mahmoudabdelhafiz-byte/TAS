import 'package:flutter_test/flutter_test.dart';
import 'package:yardsyncs_mobile/gate_pass.dart';

void main() {
  // Same vectors as tests/Tas.Application.Tests/GatePassCodeTests.cs: the app and the gate must agree.
  final secret = List<int>.generate(32, (i) => i);

  test('codes match the shared vectors', () {
    expect(gatePassCode(secret, 0), '94464393');
    expect(gatePassCode(secret, 1), '04309558');
    expect(gatePassCode(secret, 59473920), '72681261');
  });

  test('payload and countdown', () {
    final pass = GatePass(passId: 42, secret: secret);
    final at = DateTime.fromMillisecondsSinceEpoch((59473920 * 30 + 10) * 1000, isUtc: true);
    expect(pass.payload('DEMO-261020-7KQ4M', at), 'YS1:42:DEMO-261020-7KQ4M:72681261');
    expect(pass.secondsLeft(at), 20);
    expect(GatePass.fromJson(pass.toJson()).payload('X', at), 'YS1:42:X:72681261');
  });
}
