using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Tas.Application.Auth;
using Tas.Domain.Parties;

namespace Tas.Infrastructure.Auth;

/// <summary>Configuration section "Jwt". The signing key must come from configuration / environment, never from code.</summary>
public sealed class JwtOptions
{
    public string Issuer { get; set; } = "tas";
    public string Audience { get; set; } = "tas";

    /// <summary>HMAC-SHA256 key, at least 32 bytes.</summary>
    public string SigningKey { get; set; } = "";

    public int AccessTokenMinutes { get; set; } = 30;
    public int RefreshTokenDays { get; set; } = 14;

    public bool HasValidKey => Encoding.UTF8.GetByteCount(SigningKey) >= 32;

    public SymmetricSecurityKey Key() => new(Encoding.UTF8.GetBytes(SigningKey));
}

public sealed record IssuedTokens(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt);

/// <summary>Creates signed access tokens and opaque refresh tokens.</summary>
public sealed class TokenService(IOptions<JwtOptions> options, TimeProvider clock)
{
    public (string Token, DateTimeOffset ExpiresAt) CreateAccessToken(AppUser user)
    {
        var o = options.Value;
        if (!o.HasValidKey)
            throw new InvalidOperationException("Jwt:SigningKey must be at least 32 bytes.");

        var now = clock.GetUtcNow();
        var expires = now.AddMinutes(o.AccessTokenMinutes);

        var claims = new Dictionary<string, object>
        {
            [TasClaims.UserId] = user.UserId.ToString(CultureInfo.InvariantCulture),
            [TasClaims.Email] = user.Email,
            [TasClaims.Name] = user.FullName,
            [TasClaims.Role] = user.Role.ToString(),
            ["jti"] = Guid.NewGuid().ToString("N"),
        };
        if (user.CompanyId is { } companyId)
            claims[TasClaims.CompanyId] = companyId.ToString(CultureInfo.InvariantCulture);
        if (user.TerminalId is { } terminalId)
            claims[TasClaims.TerminalId] = terminalId.ToString(CultureInfo.InvariantCulture);

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = o.Issuer,
            Audience = o.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            Claims = claims,
            SigningCredentials = new SigningCredentials(o.Key(), SecurityAlgorithms.HmacSha256),
        };

        return (new JsonWebTokenHandler().CreateToken(descriptor), expires);
    }

    /// <summary>Audience of driver-app tokens; staff endpoints only accept <see cref="JwtOptions.Audience"/>.</summary>
    public static string DriverAudience(JwtOptions o) => $"{o.Audience}-driver";

    /// <summary>A driver-app session, bound to one device (checked on every request, so signing in elsewhere ends it).</summary>
    public (string Token, DateTimeOffset ExpiresAt) CreateDriverToken(int driverId, long deviceId, string mobile, int days)
    {
        var o = options.Value;
        if (!o.HasValidKey)
            throw new InvalidOperationException("Jwt:SigningKey must be at least 32 bytes.");

        var now = clock.GetUtcNow();
        var expires = now.AddDays(days);
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = o.Issuer,
            Audience = DriverAudience(o),
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            Claims = new Dictionary<string, object>
            {
                [DriverClaims.DriverId] = driverId.ToString(CultureInfo.InvariantCulture),
                [DriverClaims.DeviceId] = deviceId.ToString(CultureInfo.InvariantCulture),
                [DriverClaims.Mobile] = mobile,
                [DriverClaims.Type] = DriverClaims.DriverType,
                ["jti"] = Guid.NewGuid().ToString("N"),
            },
            SigningCredentials = new SigningCredentials(o.Key(), SecurityAlgorithms.HmacSha256),
        };
        return (new JsonWebTokenHandler().CreateToken(descriptor), expires);
    }

    /// <summary>A random, URL-safe refresh token (256 bits).</summary>
    public static string NewRefreshToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Only this hash is stored, so a database leak does not leak usable tokens.</summary>
    public static byte[] HashRefreshToken(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));
}

/// <summary>ASP.NET Core Identity's password hasher (PBKDF2 with per-password salt).</summary>
public sealed class PasswordService
{
    private static readonly PasswordHasher<AppUser> Hasher = new();
    private static readonly string DummyHash = Hasher.HashPassword(new AppUser(), "not-a-real-password-0");

    public string Hash(AppUser user, string password) => Hasher.HashPassword(user, password);

    public PasswordVerificationResult Verify(AppUser user, string password) =>
        user.PasswordHash is null
            ? PasswordVerificationResult.Failed
            : Hasher.VerifyHashedPassword(user, user.PasswordHash, password);

    /// <summary>Spends the same time as a real check, so unknown emails can't be told apart by timing.</summary>
    public void VerifyDummy(string password) => Hasher.VerifyHashedPassword(new AppUser(), DummyHash, password);
}
