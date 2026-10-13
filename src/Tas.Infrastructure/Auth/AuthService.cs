using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tas.Application.Auth;
using Tas.Domain.Parties;
using Tas.Infrastructure.Persistence;

namespace Tas.Infrastructure.Auth;

public enum AuthStatus
{
    Ok,
    InvalidCredentials,
    LockedOut,
    InvalidToken,
    Invalid,
}

public sealed record UserProfile(
    int UserId,
    string Email,
    string FullName,
    UserRole Role,
    int? CompanyId,
    string? CompanyName,
    int? TerminalId,
    string PreferredLanguage);

public sealed record AuthResult(AuthStatus Status, IssuedTokens? Tokens = null, UserProfile? User = null, string? Error = null);

/// <summary>
/// Login, token refresh, logout and password change.
///
/// - 5 wrong passwords lock the account for 15 minutes.
/// - Refresh tokens rotate on every use. Re-using an old one means it was stolen (or replayed),
///   so every session of that user is revoked.
/// - Changing the password signs the user out everywhere.
/// </summary>
public sealed class AuthService(
    TasDbContext db,
    PasswordService passwords,
    TokenService tokens,
    IOptions<JwtOptions> options,
    TimeProvider clock,
    ILogger<AuthService> logger)
{
    public const int MaxFailedLogins = 5;
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    public async Task<AuthResult> LoginAsync(string email, string password, string? ip, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var normalized = (email ?? "").Trim().ToLowerInvariant();
        var user = await db.AppUsers.SingleOrDefaultAsync(u => u.Email == normalized, ct);

        if (user is null || !user.IsActive || user.PasswordHash is null)
        {
            passwords.VerifyDummy(password ?? "");
            return new AuthResult(AuthStatus.InvalidCredentials);
        }

        if (user.LockedUntil > now)
            return new AuthResult(AuthStatus.LockedOut, Error: "Too many failed attempts. Try again in a few minutes.");

        var result = passwords.Verify(user, password ?? "");
        if (result == PasswordVerificationResult.Failed)
        {
            user.FailedLoginCount++;
            if (user.FailedLoginCount >= MaxFailedLogins)
            {
                user.LockedUntil = now + LockoutDuration;
                user.FailedLoginCount = 0;
                logger.LogWarning("User {UserId} locked out after {Count} failed logins from {Ip}", user.UserId, MaxFailedLogins, ip);
            }
            await db.SaveChangesAsync(ct);
            return new AuthResult(AuthStatus.InvalidCredentials);
        }

        if (result == PasswordVerificationResult.SuccessRehashNeeded)
            user.PasswordHash = passwords.Hash(user, password!);

        user.FailedLoginCount = 0;
        user.LockedUntil = null;
        user.LastLoginAt = now;

        var issued = await IssueAsync(user, ip, null, ct);
        return new AuthResult(AuthStatus.Ok, issued, await ProfileAsync(user, ct));
    }

    public async Task<AuthResult> RefreshAsync(string refreshToken, string? ip, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
            return new AuthResult(AuthStatus.InvalidToken);

        var now = clock.GetUtcNow();
        var hash = TokenService.HashRefreshToken(refreshToken);
        var stored = await db.RefreshTokens.AsNoTracking().SingleOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (stored is null)
            return new AuthResult(AuthStatus.InvalidToken);

        if (stored.RevokedAt is not null)
        {
            logger.LogWarning("Revoked refresh token re-used for user {UserId} from {Ip}; revoking all sessions", stored.UserId, ip);
            await RevokeAllAsync(stored.UserId, now, ct);
            return new AuthResult(AuthStatus.InvalidToken);
        }

        if (stored.ExpiresAt <= now)
            return new AuthResult(AuthStatus.InvalidToken);

        var user = await db.AppUsers.SingleOrDefaultAsync(u => u.UserId == stored.UserId && u.IsActive, ct);
        if (user is null)
            return new AuthResult(AuthStatus.InvalidToken);

        var newToken = TokenService.NewRefreshToken();
        var newHash = TokenService.HashRefreshToken(newToken);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var rotated = await db.RefreshTokens
            .Where(t => t.RefreshTokenId == stored.RefreshTokenId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.RevokedAt, now)
                .SetProperty(t => t.ReplacedByHash, newHash), ct);
        if (rotated == 0)
        {
            await tx.RollbackAsync(ct);
            return new AuthResult(AuthStatus.InvalidToken); // a parallel refresh won
        }

        var issued = await IssueAsync(user, ip, newToken, ct);
        await tx.CommitAsync(ct);
        return new AuthResult(AuthStatus.Ok, issued, await ProfileAsync(user, ct));
    }

    public async Task LogoutAsync(string refreshToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
            return;

        var now = clock.GetUtcNow();
        var hash = TokenService.HashRefreshToken(refreshToken);
        await db.RefreshTokens
            .Where(t => t.TokenHash == hash && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
    }

    public async Task<AuthResult> ChangePasswordAsync(int userId, string currentPassword, string newPassword, CancellationToken ct = default)
    {
        var user = await db.AppUsers.SingleOrDefaultAsync(u => u.UserId == userId && u.IsActive, ct);
        if (user is null || passwords.Verify(user, currentPassword ?? "") == PasswordVerificationResult.Failed)
            return new AuthResult(AuthStatus.InvalidCredentials, Error: "Current password is wrong.");

        var problem = PasswordPolicy.Check(newPassword, user.Email);
        if (problem is not null)
            return new AuthResult(AuthStatus.Invalid, Error: problem);

        user.PasswordHash = passwords.Hash(user, newPassword);
        await db.SaveChangesAsync(ct);
        await RevokeAllAsync(user.UserId, clock.GetUtcNow(), ct);
        return new AuthResult(AuthStatus.Ok, User: await ProfileAsync(user, ct));
    }

    public async Task<UserProfile?> GetProfileAsync(int userId, CancellationToken ct = default)
    {
        var user = await db.AppUsers.AsNoTracking().SingleOrDefaultAsync(u => u.UserId == userId && u.IsActive, ct);
        return user is null ? null : await ProfileAsync(user, ct);
    }

    internal async Task<UserProfile> ProfileAsync(AppUser user, CancellationToken ct)
    {
        var companyName = user.CompanyId is null
            ? null
            : await db.Companies.AsNoTracking().Where(c => c.CompanyId == user.CompanyId).Select(c => c.Name).SingleOrDefaultAsync(ct);
        return new UserProfile(user.UserId, user.Email, user.FullName, user.Role, user.CompanyId, companyName, user.TerminalId, user.PreferredLanguage.Trim());
    }

    private async Task<IssuedTokens> IssueAsync(AppUser user, string? ip, string? refreshToken, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var (access, accessExpires) = tokens.CreateAccessToken(user);
        refreshToken ??= TokenService.NewRefreshToken();
        var refreshExpires = now.AddDays(options.Value.RefreshTokenDays);

        db.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.UserId,
            TokenHash = TokenService.HashRefreshToken(refreshToken),
            CreatedAt = now,
            ExpiresAt = refreshExpires,
            CreatedIp = ip is { Length: > 45 } ? ip[..45] : ip,
        });
        await db.SaveChangesAsync(ct);

        return new IssuedTokens(access, accessExpires, refreshToken, refreshExpires);
    }

    private Task RevokeAllAsync(int userId, DateTimeOffset now, CancellationToken ct) =>
        db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
}
