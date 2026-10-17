using System.Text;
using Yardsyncs.Application.Auth;
using Yardsyncs.Domain.Parties;

namespace Yardsyncs.Application.Tests;

public class TotpTests
{
    private static readonly byte[] Rfc = Encoding.ASCII.GetBytes("12345678901234567890");

    [Theory] // RFC 6238 appendix B (SHA-1), last 8 and 6 digits
    [InlineData(59, "94287082")]
    [InlineData(1111111109, "07081804")]
    [InlineData(1234567890, "89005924")]
    [InlineData(2000000000, "69279037")]
    public void Codes_are_the_rfcs(long unixSeconds, string expected)
    {
        var step = Totp.Step(DateTimeOffset.FromUnixTimeSeconds(unixSeconds));
        Assert.Equal(expected, Totp.Code(Rfc, step, 8));
        Assert.Equal(expected[2..], Totp.Code(Rfc, step));
    }

    [Fact]
    public void A_code_works_within_a_step_either_side_and_only_once()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var step = Totp.Step(now);
        Assert.Equal(step, Totp.Verify(Rfc, Totp.Code(Rfc, step), now, null));
        Assert.Equal(step - 1, Totp.Verify(Rfc, Totp.Code(Rfc, step - 1), now, null));       // the phone is a little behind
        Assert.Null(Totp.Verify(Rfc, Totp.Code(Rfc, step - 2), now, null));                  // too old
        Assert.Null(Totp.Verify(Rfc, Totp.Code(Rfc, step), now, step));                      // used already
        Assert.Equal(step, Totp.Verify(Rfc, Totp.Code(Rfc, step)[..3] + " " + Totp.Code(Rfc, step)[3..], now, null)); // "123 456"
        Assert.Null(Totp.Verify(Rfc, "12345", now, null));
        Assert.Null(Totp.Verify(Rfc, null, now, null));
    }

    [Fact]
    public void The_secret_travels_as_base32_and_an_otpauth_link()
    {
        Assert.Equal("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", Totp.Base32(Rfc));
        Assert.Equal(Rfc, Totp.FromBase32("gezd gnbv gy3t qojq gezd gnbv gy3t qojq"));
        var secret = Totp.NewSecret();
        Assert.Equal(secret, Totp.FromBase32(Totp.Base32(secret)));
        Assert.Equal("otpauth://totp/Harbor%20One:admin%40x.example?secret=GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ&issuer=Harbor%20One&digits=6&period=30",
            Totp.Uri("Harbor One", "admin@x.example", Rfc));
    }

    [Fact]
    public void Admins_are_the_ones_who_must_use_it()
    {
        Assert.True(Totp.IsAdminRole(UserRole.TerminalAdmin));
        Assert.True(Totp.IsAdminRole(UserRole.SysAdmin));
        Assert.True(Totp.IsAdminRole(UserRole.OperatorAdmin));
        Assert.False(Totp.IsAdminRole(UserRole.Planner));
        Assert.False(Totp.IsAdminRole(UserRole.CompanyAdmin));
    }
}
