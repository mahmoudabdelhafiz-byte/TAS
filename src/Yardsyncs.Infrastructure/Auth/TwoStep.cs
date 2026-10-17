using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Yardsyncs.Application.Auth;
using Yardsyncs.Application.Capacity;
using Yardsyncs.Domain;
using Yardsyncs.Domain.Parties;
using Yardsyncs.Infrastructure.Persistence;

namespace Yardsyncs.Infrastructure.Auth;

/// <summary>Whether admins must use two-step sign-in (Auth:AdminTwoStep: Required, the default, or Optional).</summary>
public sealed class TwoStepPolicy(IConfiguration config)
{
    public bool AdminsMustUseIt => !string.Equals(config["Auth:AdminTwoStep"], "Optional", StringComparison.OrdinalIgnoreCase);

    /// <summary>Signed in, but must set up two-step sign-in before doing anything else.</summary>
    public bool SetupRequired(UserRole role, DateTimeOffset? enabledAt) => AdminsMustUseIt && Totp.IsAdminRole(role) && enabledAt is null;
}

/// <summary>
/// The secrets of two-step sign-in: authenticator secrets encrypted at rest (AES-GCM), and the short-lived ticket that
/// carries a user from the password to the code. Keys from TwoStep:Key (32 bytes, base64) or derived from the JWT key.
/// </summary>
public sealed class TwoStepCrypto(IOptions<JwtOptions> jwt, IConfiguration config)
{
    /// <summary>How long after the password the code may be typed.</summary>
    public static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(5);

    public byte[] Protect(byte[] secret)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[secret.Length];
        using var aes = new AesGcm(Key("yardsyncs-two-step-secret-v1"), 16);
        aes.Encrypt(nonce, secret, cipher, tag);
        return [.. nonce, .. tag, .. cipher];
    }

    public byte[] Unprotect(byte[] stored)
    {
        var secret = new byte[stored.Length - 28];
        using var aes = new AesGcm(Key("yardsyncs-two-step-secret-v1"), 16);
        aes.Decrypt(stored.AsSpan(0, 12), stored.AsSpan(28), stored.AsSpan(12, 16), secret);
        return secret;
    }

    /// <summary>A ticket: "the password of this user was right at this time". Signed; valid for a few minutes.</summary>
    public string Challenge(int userId, DateTimeOffset now)
    {
        var payload = string.Create(CultureInfo.InvariantCulture,
            $"{userId}:{(now + ChallengeLifetime).ToUnixTimeSeconds()}:{Convert.ToHexString(RandomNumberGenerator.GetBytes(8))}");
        return $"{Encode(Encoding.UTF8.GetBytes(payload))}.{Encode(Sign(payload))}";
    }

    /// <summary>The user a ticket is for, while it is valid and untouched.</summary>
    public int? Read(string? challenge, DateTimeOffset now)
    {
        var parts = (challenge ?? "").Split('.');
        if (parts.Length != 2)
            return null;
        try
        {
            var payload = Encoding.UTF8.GetString(Decode(parts[0]));
            if (!CryptographicOperations.FixedTimeEquals(Sign(payload), Decode(parts[1])))
                return null;
            var fields = payload.Split(':');
            return fields.Length == 3
                   && int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var userId)
                   && long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var expires)
                   && now.ToUnixTimeSeconds() <= expires
                ? userId
                : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private byte[] Sign(string payload) => HMACSHA256.HashData(Key("yardsyncs-two-step-challenge-v1"), Encoding.UTF8.GetBytes(payload));

    private byte[] Key(string purpose)
    {
        var configured = config["TwoStep:Key"];
        var root = string.IsNullOrWhiteSpace(configured) ? Encoding.UTF8.GetBytes(jwt.Value.SigningKey) : Convert.FromBase64String(configured);
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, root, 32, info: Encoding.UTF8.GetBytes(purpose));
    }

    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Decode(string text)
    {
        var s = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}

/// <summary>What the authenticator app needs to be set up: the secret typed in, or the link (a QR code of it).</summary>
public sealed record TwoStepSetupView(string Secret, string Uri);

/// <summary>Setting up, turning off and resetting two-step sign-in.</summary>
public sealed class TwoStepService(
    YardsyncsDbContext db, TwoStepCrypto crypto, TwoStepPolicy policy, PasswordService passwords, IConfiguration config, TimeProvider clock, ILogger<TwoStepService> logger)
{
    /// <summary>A new secret for the user's authenticator app; on only once confirmed with a code from it.</summary>
    public async Task<AdminResult<TwoStepSetupView>> SetupAsync(CurrentUser me, CancellationToken ct = default)
    {
        var user = await db.AppUsers.SingleAsync(u => u.UserId == me.UserId, ct);
        if (user.TotpEnabledAt is not null)
            return AdminResult<TwoStepSetupView>.Conflict("Two-step sign-in is already on. Turn it off first to use another phone.");
        var secret = Totp.NewSecret();
        user.TotpSecret = crypto.Protect(secret);
        user.TotpLastStep = null;
        await db.SaveChangesAsync(ct);
        var issuer = string.IsNullOrWhiteSpace(config["Brand"]) ? "Yardsyncs" : config["Brand"]!.Trim();
        return AdminResult<TwoStepSetupView>.Ok(new TwoStepSetupView(Group(Totp.Base32(secret)), Totp.Uri(issuer, user.Email, secret)));
    }

    /// <summary>The first code from the app turns two-step sign-in on.</summary>
    public async Task<AdminResult<bool>> ConfirmAsync(CurrentUser me, string? code, CancellationToken ct = default)
    {
        var user = await db.AppUsers.SingleAsync(u => u.UserId == me.UserId, ct);
        if (user.TotpSecret is null)
            return AdminResult<bool>.Conflict("Start the set-up first.");
        if (user.TotpEnabledAt is not null)
            return AdminResult<bool>.Ok(true);
        var now = clock.GetUtcNow();
        if (Totp.Verify(crypto.Unprotect(user.TotpSecret), code, now, null) is not { } step)
            return AdminResult<bool>.Invalid("That code isn't right. Check the phone's time and type the code the app shows now.");
        user.TotpEnabledAt = now;
        user.TotpLastStep = step;
        AuditWriter.Add(db, user.TerminalId, "app_user", user.UserId, AuditAction.Update, null, new { TwoStep = "on" }, "Two-step sign-in turned on", user.UserId, now);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Two-step sign-in turned on for user {UserId}", user.UserId);
        return AdminResult<bool>.Ok(true);
    }

    /// <summary>Off, with the password and a code. Admins can't while the installation requires it for them.</summary>
    public async Task<AdminResult<bool>> DisableAsync(CurrentUser me, string? password, string? code, CancellationToken ct = default)
    {
        var user = await db.AppUsers.SingleAsync(u => u.UserId == me.UserId, ct);
        if (user.TotpEnabledAt is null)
            return AdminResult<bool>.Ok(false);
        if (policy.AdminsMustUseIt && Totp.IsAdminRole(user.Role))
            return AdminResult<bool>.Forbidden("Admins must keep two-step sign-in. To move to another phone, ask another admin to reset it.");
        var now = clock.GetUtcNow();
        if (passwords.Verify(user, password ?? "") == PasswordVerificationResult.Failed
            || Totp.Verify(crypto.Unprotect(user.TotpSecret!), code, now, user.TotpLastStep) is null)
            return AdminResult<bool>.Invalid("The password or the code isn't right.");
        Clear(user);
        AuditWriter.Add(db, user.TerminalId, "app_user", user.UserId, AuditAction.Update, null, new { TwoStep = "off" }, "Two-step sign-in turned off", user.UserId, now);
        await db.SaveChangesAsync(ct);
        return AdminResult<bool>.Ok(false);
    }

    /// <summary>A lost phone: an admin who manages the user turns it off; the user sets it up again at the next sign-in.</summary>
    public async Task ResetAsync(AppUser user, CurrentUser actor, CancellationToken ct)
    {
        Clear(user);
        AuditWriter.Add(db, user.TerminalId, "app_user", user.UserId, AuditAction.Update, null, new { TwoStep = "reset" }, "Two-step sign-in reset", actor.UserId, clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Two-step sign-in of user {UserId} reset by user {ActorId}", user.UserId, actor.UserId);
    }

    private static void Clear(AppUser user)
    {
        user.TotpSecret = null;
        user.TotpEnabledAt = null;
        user.TotpLastStep = null;
    }

    private static string Group(string text) => string.Join(' ', text.Chunk(4).Select(c => new string(c)));
}
