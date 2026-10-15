using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Tas.Application.Auth;
using Tas.Application.Gate;
using Tas.Domain.Parties;
using Tas.Infrastructure.Auth;
using Tas.Infrastructure.Persistence;

namespace Tas.Infrastructure.DriverApp;

/// <summary>What the app needs to make rotating gate codes offline.</summary>
public sealed record GatePassView(long PassId, string Secret, string Prefix, int StepSeconds, int Digits);

/// <param name="AppointmentNo">The appointment the pass was shown for, when valid.</param>
public sealed record GatePassCheck(bool Valid, string? AppointmentNo, string? Problem);

/// <summary>
/// Issues the driver's gate pass secret and checks rotating codes at the gate. Secrets are stored encrypted
/// (AES-GCM; key from GatePass:Key, or derived from the JWT signing key).
/// </summary>
public sealed class GatePassService(TasDbContext db, IOptions<JwtOptions> jwt, IConfiguration config, TimeProvider clock)
{
    public async Task<GatePassView> IssueAsync(CurrentDriver driver, CancellationToken ct = default)
    {
        var pass = await db.GatePasses
            .Where(p => p.DriverId == driver.DriverId && p.Status == "ACTIVE")
            .OrderByDescending(p => p.GatePassId)
            .FirstOrDefaultAsync(ct);

        byte[] secret;
        if (pass is null)
        {
            secret = RandomNumberGenerator.GetBytes(32);
            pass = new GatePass { DriverId = driver.DriverId, PassSecret = Protect(secret), IssuedAt = clock.GetUtcNow() };
            db.GatePasses.Add(pass);
            await db.SaveChangesAsync(ct);
        }
        else
        {
            secret = Unprotect(pass.PassSecret);
        }

        return new GatePassView(pass.GatePassId, Convert.ToBase64String(secret), GatePassCode.Prefix, GatePassCode.StepSeconds, GatePassCode.Digits);
    }

    /// <summary>A new phone gets a new secret: the old phone's codes stop working.</summary>
    public Task RevokeAsync(IReadOnlyCollection<int> driverIds, string reason, CancellationToken ct = default) =>
        db.GatePasses
            .Where(p => driverIds.Contains(p.DriverId) && p.Status == "ACTIVE")
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, "REVOKED").SetProperty(p => p.RevokedReason, reason), ct);

    /// <summary>Checks a gate pass QR read at a lane of this terminal.</summary>
    public async Task<GatePassCheck> VerifyAsync(int terminalId, string payload, CancellationToken ct = default)
    {
        if (!GatePassCode.TryParse(payload, out var passId, out var number, out var code))
            return new GatePassCheck(false, null, "Not a Yardsyncs gate pass.");

        var now = clock.GetUtcNow();
        var pass = await db.GatePasses.AsNoTracking().SingleOrDefaultAsync(p => p.GatePassId == passId, ct);
        if (pass is null || pass.Status != "ACTIVE" || (pass.ExpiresAt is { } expires && expires <= now)
            || (pass.TerminalId is { } t && t != terminalId))
            return new GatePassCheck(false, null, "The gate pass is no longer valid (the driver signed in on another phone or it was revoked).");

        if (!GatePassCode.Verify(Unprotect(pass.PassSecret), code, now))
            return new GatePassCheck(false, null, "The gate pass code has expired — ask the driver to show the live code in the app.");

        // The appointment must be this driver's (same person: same mobile number, any company).
        var mobile = await db.Drivers.AsNoTracking().Where(d => d.DriverId == pass.DriverId && !d.IsBlocked).Select(d => d.Mobile).SingleOrDefaultAsync(ct);
        var owns = mobile is not null && await db.Appointments.AsNoTracking().AnyAsync(a =>
            a.TerminalId == terminalId && a.AppointmentNo == number && a.DriverId != null
            && db.Drivers.Any(d => d.DriverId == a.DriverId && d.Mobile == mobile), ct);

        return owns
            ? new GatePassCheck(true, number, null)
            : new GatePassCheck(false, null, $"The gate pass is valid, but appointment {number} is not this driver's.");
    }

    private byte[] Key()
    {
        var configured = config["GatePass:Key"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var bytes = Convert.FromBase64String(configured);
            if (bytes.Length == 32)
                return bytes;
            throw new InvalidOperationException("GatePass:Key must be 32 bytes, base64.");
        }
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(jwt.Value.SigningKey), 32, info: Encoding.UTF8.GetBytes("yardsyncs-gate-pass-v1"));
    }

    private byte[] Protect(byte[] secret)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[secret.Length];
        using var aes = new AesGcm(Key(), 16);
        aes.Encrypt(nonce, secret, cipher, tag);
        var stored = new byte[nonce.Length + tag.Length + cipher.Length];
        nonce.CopyTo(stored, 0);
        tag.CopyTo(stored, 12);
        cipher.CopyTo(stored, 28);
        return stored;
    }

    private byte[] Unprotect(byte[] stored)
    {
        var nonce = stored.AsSpan(0, 12);
        var tag = stored.AsSpan(12, 16);
        var cipher = stored.AsSpan(28);
        var secret = new byte[cipher.Length];
        using var aes = new AesGcm(Key(), 16);
        aes.Decrypt(nonce, cipher, tag, secret);
        return secret;
    }
}
