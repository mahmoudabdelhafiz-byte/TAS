using Microsoft.Extensions.Options;
using Tas.Infrastructure.Auth;

namespace Tas.Infrastructure.Tests;

public class PayloadProtectorTests
{
    private static PayloadProtector With(string key) => new(Options.Create(new JwtOptions { SigningKey = key }));

    private static readonly PayloadProtector Protector = With("test-only-signing-key-0123456789-abcdefghijkl");

    [Fact]
    public void Round_trips_and_never_shows_the_secret()
    {
        const string secret = "yS3x-token_ابc";
        var a = Protector.Protect(secret);
        var b = Protector.Protect(secret);

        Assert.Equal(secret, Protector.Unprotect(a));
        Assert.NotEqual(a, b); // random nonce
        Assert.DoesNotContain("token", a);
    }

    [Fact]
    public void Tampered_foreign_or_garbage_payloads_are_rejected()
    {
        var protectedText = Protector.Protect("secret");
        var bytes = Convert.FromBase64String(protectedText);
        bytes[^1] ^= 1;

        Assert.Null(Protector.Unprotect(Convert.ToBase64String(bytes)));
        Assert.Null(With("another-signing-key-0123456789-abcdefghijklmn").Unprotect(protectedText));
        Assert.Null(Protector.Unprotect("not base64!"));
        Assert.Null(Protector.Unprotect("AAAA"));
        Assert.Null(Protector.Unprotect(null));
    }

    [Fact]
    public void A_short_key_is_refused() =>
        Assert.Throws<InvalidOperationException>(() => With("too-short"));
}
