using Yardsyncs.Application.Gate;

namespace Yardsyncs.Application.Tests;

public class GateSystemKeysTests
{
    [Fact]
    public void Keys_are_new_each_time_recognisable_and_kept_only_as_a_fingerprint()
    {
        var a = GateSystemKeys.New();
        var b = GateSystemKeys.New();
        Assert.NotEqual(a, b);
        Assert.True(GateSystemKeys.LooksLikeKey(a));
        Assert.False(GateSystemKeys.LooksLikeKey(DeviceKeys.New())); // a device key is not a gate system key
        Assert.False(GateSystemKeys.LooksLikeKey("ysg_short"));
        Assert.Equal(64, GateSystemKeys.Hash(a).Length);
        Assert.Equal(GateSystemKeys.Hash(a), GateSystemKeys.Hash($" {a} "));
        Assert.DoesNotContain(a[..20], GateSystemKeys.Hint(a));
        Assert.EndsWith(a[^6..], GateSystemKeys.Hint(a));
    }

    [Theory]
    [InlineData("North gate OCR portal", true)]
    [InlineData("  ", false)]
    [InlineData("", false)]
    public void A_gate_system_needs_a_name(string name, bool ok) =>
        Assert.Equal(ok, GateSystemKeys.Validate(new GateSystemInput(name)).Count == 0);

    [Fact]
    public void Names_are_at_most_60_characters() =>
        Assert.NotEmpty(GateSystemKeys.Validate(new GateSystemInput(new string('x', 61))));
}
