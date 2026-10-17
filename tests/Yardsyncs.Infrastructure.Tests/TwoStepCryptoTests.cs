using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Yardsyncs.Infrastructure.Auth;

namespace Yardsyncs.Infrastructure.Tests;

/// <summary>Two-step sign-in's secrets (no database needed).</summary>
public class TwoStepCryptoTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 8, 0, 0, TimeSpan.Zero);

    private static TwoStepCrypto Crypto(string signingKey = "a-signing-key-of-at-least-32-bytes-long!") =>
        new(Options.Create(new JwtOptions { SigningKey = signingKey }), new ConfigurationBuilder().Build());

    [Fact]
    public void Secrets_are_stored_encrypted_and_come_back()
    {
        var crypto = Crypto();
        var secret = Yardsyncs.Application.Auth.Totp.NewSecret();
        var stored = crypto.Protect(secret);
        Assert.NotEqual(secret, stored[28..]);
        Assert.Equal(secret, crypto.Unprotect(stored));
        Assert.NotEqual(stored, crypto.Protect(secret)); // a new nonce each time
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => Crypto("another-signing-key-of-32-bytes-or-more").Unprotect(stored));
    }

    [Fact]
    public void The_password_ticket_names_its_user_for_five_minutes_and_cant_be_altered()
    {
        var crypto = Crypto();
        var ticket = crypto.Challenge(42, Now);
        Assert.Equal(42, crypto.Read(ticket, Now.AddMinutes(4)));
        Assert.Null(crypto.Read(ticket, Now.AddMinutes(6)));
        Assert.Null(Crypto("another-signing-key-of-32-bytes-or-more").Read(ticket, Now));

        var parts = ticket.Split('.');
        var forged = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"1:{Now.AddHours(1).ToUnixTimeSeconds()}:00")).TrimEnd('=') + "." + parts[1];
        Assert.Null(crypto.Read(forged, Now));
        Assert.Null(crypto.Read("nonsense", Now));
        Assert.Null(crypto.Read(null, Now));
    }
}
