using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tas.Application.MasterData;
using Tas.Application.Languages;
using Tas.Application.Notifications;
using Tas.Domain;
using Tas.Domain.Parties;
using Tas.Infrastructure.Auth;
using Tas.Infrastructure.Notifications;
using Tas.Infrastructure.Persistence;

namespace Tas.Infrastructure.DriverApp;

/// <summary>Configuration section "DriverLogin".</summary>
public sealed class DriverLoginOptions
{
    public int CodeMinutes { get; set; } = 5;

    /// <summary>Codes one mobile number may ask for within 15 minutes.</summary>
    public int MaxCodesPer15Minutes { get; set; } = 3;

    /// <summary>Codes one mobile number may ask for in 24 hours, so guessing can't go on all day.</summary>
    public int MaxCodesPerDay { get; set; } = 10;

    /// <summary>Wrong tries before a code stops working.</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>How long the app stays signed in on its phone (ends earlier if the driver signs in on another phone).</summary>
    public int SessionDays { get; set; } = 30;

    /// <summary>Tests only: return the code in the response instead of relying on SMS. Never enable in production.</summary>
    public bool ExposeCodeForTesting { get; set; }
}

public enum DriverLoginStatus
{
    Ok,
    Invalid,
    TooMany,
}

/// <param name="TestCode">Only set when <see cref="DriverLoginOptions.ExposeCodeForTesting"/> is on.</param>
public sealed record DriverLoginStart(DriverLoginStatus Status, string? Error = null, string? TestCode = null);

public sealed record DriverSession(string Token, DateTimeOffset ExpiresAt, DriverProfile Driver);

public sealed record DriverProfile(int DriverId, string FullName, string Mobile, string PreferredLanguage, IReadOnlyList<string> Companies);

public sealed record DriverLoginResult(DriverLoginStatus Status, DriverSession? Session = null, string? Error = null);

/// <summary>
/// Driver sign-in with a one-time code by SMS. The answer to "send me a code" is the same whether or not the number
/// belongs to a driver, so the endpoint can't be used to find out who drives for whom.
/// </summary>
public sealed partial class DriverLoginService(
    TasDbContext db,
    TokenService tokens,
    NotificationSenders senders,
    IOptions<DriverLoginOptions> options,
    TimeProvider clock,
    ILogger<DriverLoginService> logger,
    TerminalProfiles? profiles = null)
{
    public const string WrongCode = "The code is wrong or has expired. Ask for a new one.";

    [GeneratedRegex("^[A-Za-z0-9_-]{8,64}$")]
    private static partial Regex InstallIdFormat();

    public async Task<DriverLoginStart> StartAsync(string? mobileInput, string? installId, CancellationToken ct = default)
    {
        var o = options.Value;
        var mobile = PhoneNumber.Normalize(mobileInput);
        if (mobile is null)
            return new DriverLoginStart(DriverLoginStatus.Invalid, "Enter a valid mobile number.");
        if (installId is null || !InstallIdFormat().IsMatch(installId))
            return new DriverLoginStart(DriverLoginStatus.Invalid, "Unknown app install.");

        var now = clock.GetUtcNow();
        var recent = await db.DriverOtps.CountAsync(x => x.Mobile == mobile && x.CreatedAt > now.AddMinutes(-15), ct);
        if (recent >= o.MaxCodesPer15Minutes)
            return new DriverLoginStart(DriverLoginStatus.TooMany, "Too many codes asked for this number. Try again in 15 minutes.");
        var today = await db.DriverOtps.CountAsync(x => x.Mobile == mobile && x.CreatedAt > now.AddHours(-24), ct);
        if (today >= o.MaxCodesPerDay)
            return new DriverLoginStart(DriverLoginStatus.TooMany, "Too many codes asked for this number today. Try again tomorrow or ask your company.");

        var driver = await db.Drivers.AsNoTracking()
            .Where(d => d.Mobile == mobile && !d.IsBlocked)
            .OrderBy(d => d.DriverId)
            .FirstOrDefaultAsync(ct);

        // Every request counts against the limit, known number or not.
        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
        db.DriverOtps.Add(new DriverOtp
        {
            Mobile = mobile,
            InstallId = installId,
            CodeHash = Hash(mobile, installId, code),
            ExpiresAt = now.AddMinutes(o.CodeMinutes),
            CreatedAt = now,
        });
        await db.SaveChangesAsync(ct);

        if (driver is null)
            return new DriverLoginStart(DriverLoginStatus.Ok);

        var sender = senders.For(NotificationChannel.Sms);
        if (sender is null)
        {
            logger.LogWarning("No SMS sender configured; driver sign-in code not sent");
        }
        else
        {
            try
            {
                var message = CodeMessage(driver.PreferredLanguage.Trim(), code, o.CodeMinutes);
                var brand = profiles?.BrandFor(null) ?? TerminalProfiles.DefaultBrand; // drivers work at several terminals
                if (brand != TerminalProfiles.DefaultBrand)
                    message = new RenderedMessage(brand, message.Body.Replace(TerminalProfiles.DefaultBrand, brand));
                await sender.SendAsync(mobile, message, ct);
            }
            catch (NotificationSendException ex)
            {
                logger.LogWarning(ex, "Sign-in code could not be sent to driver {DriverId}", driver.DriverId);
            }
        }

        return new DriverLoginStart(DriverLoginStatus.Ok, TestCode: o.ExposeCodeForTesting ? code : null);
    }

    public async Task<DriverLoginResult> VerifyAsync(
        string? mobileInput, string? installId, string? code, string? platform, string? appVersion, CancellationToken ct = default)
    {
        var o = options.Value;
        var mobile = PhoneNumber.Normalize(mobileInput);
        if (mobile is null || installId is null || !InstallIdFormat().IsMatch(installId) || code is null || code.Trim().Length != 6)
            return new DriverLoginResult(DriverLoginStatus.Invalid, Error: WrongCode);

        var now = clock.GetUtcNow();
        var otp = await db.DriverOtps.AsNoTracking()
            .Where(x => x.Mobile == mobile && x.InstallId == installId && x.ConsumedAt == null && x.ExpiresAt > now)
            .OrderByDescending(x => x.OtpId)
            .FirstOrDefaultAsync(ct);
        if (otp is null)
            return new DriverLoginResult(DriverLoginStatus.Invalid, Error: WrongCode);

        // Count the try in the database first, so tries sent at the same moment can't get past the limit.
        var tried = await db.DriverOtps
            .Where(x => x.OtpId == otp.OtpId && x.ConsumedAt == null && x.Attempts < o.MaxAttempts)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Attempts, x => x.Attempts + 1), ct);
        if (tried == 0)
            return new DriverLoginResult(DriverLoginStatus.Invalid, Error: WrongCode);

        var match = CryptographicOperations.FixedTimeEquals(otp.CodeHash, Hash(mobile, installId, code.Trim()));
        if (!match)
            return new DriverLoginResult(DriverLoginStatus.Invalid, Error: WrongCode);

        var drivers = await db.Drivers.Where(d => d.Mobile == mobile && !d.IsBlocked).OrderBy(d => d.DriverId).ToListAsync(ct);
        if (drivers.Count == 0)
            return new DriverLoginResult(DriverLoginStatus.Invalid, Error: WrongCode);

        // A code signs in once, also when the right code is sent twice at the same moment.
        var consumed = await db.DriverOtps
            .Where(x => x.OtpId == otp.OtpId && x.ConsumedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ConsumedAt, now), ct);
        if (consumed == 0)
            return new DriverLoginResult(DriverLoginStatus.Invalid, Error: WrongCode);
        var driverIds = drivers.Select(d => d.DriverId).ToList();
        var driver = drivers[0];

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // One phone per driver: signing in here signs the other phone out.
        await db.DriverDevices
            .Where(d => driverIds.Contains(d.DriverId) && d.IsActive && d.InstallId != installId)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.IsActive, false), ct);

        var device = await db.DriverDevices.SingleOrDefaultAsync(d => d.InstallId == installId, ct);
        if (device is null)
        {
            device = new DriverDevice { InstallId = installId, RegisteredAt = now };
            db.DriverDevices.Add(device);
        }
        device.DriverId = driver.DriverId;
        device.Platform = string.Equals(platform, "ios", StringComparison.OrdinalIgnoreCase) ? "IOS" : "ANDROID";
        device.AppVersion = appVersion is { Length: > 0 and <= 20 } ? appVersion : null;
        device.IsActive = true;
        device.LastSeenAt = now;

        // The old phone's gate pass stops working; this phone gets a new one.
        await db.GatePasses
            .Where(p => driverIds.Contains(p.DriverId) && p.Status == "ACTIVE")
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, "REVOKED").SetProperty(p => p.RevokedReason, "Signed in on another phone"), ct);

        db.DriverVerifications.Add(new DriverVerification
        {
            DriverId = driver.DriverId,
            Method = "MOBILE_OTP",
            Status = "VERIFIED",
            VerifiedAt = now,
            CreatedAt = now,
        });

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        var companyIds = drivers.Select(d => d.CompanyId).Distinct().ToList();
        var companies = await db.Companies.AsNoTracking()
            .Where(c => companyIds.Contains(c.CompanyId))
            .Select(c => c.Name)
            .ToListAsync(ct);

        var (token, expires) = tokens.CreateDriverToken(driver.DriverId, device.DeviceId, mobile, o.SessionDays);
        return new DriverLoginResult(DriverLoginStatus.Ok, new DriverSession(token, expires,
            new DriverProfile(driver.DriverId, driver.FullName, mobile, driver.PreferredLanguage.Trim(), companies)));
    }

    /// <summary>Stores the app's push token for this phone (null clears it). Returns an error, or null.</summary>
    public async Task<string?> SetPushTokenAsync(long deviceId, string? token, CancellationToken ct = default)
    {
        token = string.IsNullOrWhiteSpace(token) ? null : token.Trim();
        if (token is { Length: > 400 })
            return "The push token is too long.";

        // A token belongs to one install: if the app was reinstalled, the old row must not get pushes.
        if (token is not null)
            await db.DriverDevices.Where(d => d.PushToken == token && d.DeviceId != deviceId)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.PushToken, (string?)null), ct);
        await db.DriverDevices.Where(d => d.DeviceId == deviceId)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.PushToken, token).SetProperty(d => d.LastSeenAt, clock.GetUtcNow()), ct);
        return null;
    }

    /// <summary>Signs this phone out.</summary>
    public Task LogoutAsync(long deviceId, CancellationToken ct = default) =>
        db.DriverDevices.Where(d => d.DeviceId == deviceId)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.IsActive, false).SetProperty(d => d.PushToken, (string?)null), ct);

    private static byte[] Hash(string mobile, string installId, string code) =>
        SHA256.HashData(Encoding.UTF8.GetBytes($"{mobile}|{installId}|{code}"));

    /// <summary>The SMS with the code, in the driver's language (a language pack can add or correct it).</summary>
    public static RenderedMessage CodeMessage(string language, string code, int minutes) =>
        new("Yardsyncs", NotificationText.Render(LanguageCatalog.Messages.SignInCode, language, new Dictionary<string, string>
        {
            ["code"] = code,
            ["minutes"] = minutes.ToString(System.Globalization.CultureInfo.InvariantCulture),
        }).Body);
}
