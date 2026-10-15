using Tas.Application.Gate;

namespace Tas.Application.Tests;

public class GatePassCodeTests
{
    // Same vectors as mobile/test/gate_pass_test.dart: the app and the gate must agree.
    private static readonly byte[] Secret = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    [Theory]
    [InlineData(0L, "94464393")]
    [InlineData(1L, "04309558")]
    [InlineData(59473920L, "72681261")]
    public void Codes_match_the_shared_vectors(long step, string code) =>
        Assert.Equal(code, GatePassCode.Compute(Secret, step));

    [Fact]
    public void Codes_of_the_neighbouring_steps_are_accepted_older_ones_are_not()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(59473920L * 30 + 10);
        Assert.True(GatePassCode.Verify(Secret, "72681261", now));
        Assert.True(GatePassCode.Verify(Secret, GatePassCode.Compute(Secret, 59473919), now));
        Assert.True(GatePassCode.Verify(Secret, GatePassCode.Compute(Secret, 59473921), now));
        Assert.False(GatePassCode.Verify(Secret, GatePassCode.Compute(Secret, 59473917), now));
        Assert.False(GatePassCode.Verify(Secret, "00000000", now));
    }

    [Fact]
    public void Payload_round_trip_and_rejects()
    {
        var payload = GatePassCode.Payload(42, "DEMO-261020-7KQ4M", "04309558");
        Assert.Equal("YS1:42:DEMO-261020-7KQ4M:04309558", payload);
        Assert.True(GatePassCode.TryParse(payload.ToLowerInvariant(), out var id, out var number, out var code));
        Assert.Equal(42, id);
        Assert.Equal("DEMO-261020-7KQ4M", number);
        Assert.Equal("04309558", code);

        Assert.False(GatePassCode.TryParse("ABCDEF0123456789", out _, out _, out _)); // static appointment QR
        Assert.False(GatePassCode.TryParse("YS1:x:DEMO:04309558", out _, out _, out _));
        Assert.False(GatePassCode.TryParse("YS1:1:DEMO:123", out _, out _, out _));
        Assert.False(GatePassCode.TryParse(null, out _, out _, out _));
    }
}
