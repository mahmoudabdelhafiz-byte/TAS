using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tas.Application.Auth;
using Tas.Domain;
using Tas.Domain.Parties;
using Tas.Infrastructure.Persistence;

namespace Tas.Infrastructure.Auth;

public sealed class PasswordTokenOptions
{
    /// <summary>How long a forgotten-password link works.</summary>
    public int ResetMinutes { get; set; } = 60;

    /// <summary>How long an invitation link works.</summary>
    public int InviteDays { get; set; } = 7;

    /// <summary>More forgotten-password requests than this per user and hour are ignored (no email flooding).</summary>
    public int MaxResetRequestsPerHour { get; set; } = 3;

    public TimeSpan ValidFor(PasswordTokenPurpose purpose) =>
        purpose == PasswordTokenPurpose.Invite ? TimeSpan.FromDays(InviteDays) : TimeSpan.FromMinutes(ResetMinutes);
}

/// <summary>What the portal shows on the "set your password" page before the user types it.</summary>
public sealed record PasswordTokenInfo(PasswordTokenPurpose Purpose, string Email, string FullName, DateTimeOffset ExpiresAt);

public enum PasswordTokenStatus
{
    Ok,
    Invalid,
}

/// <summary>
/// One-time password links: forgotten password (self-service) and invitations for new users.
///
/// - Only the SHA-256 hash of a token is stored. The token travels to the email sender encrypted in the
///   notification outbox and is wiped from there once the email has gone out.
/// - A new link cancels the earlier ones of the same kind; setting a password by any route cancels all.
/// - Forgotten-password requests never reveal whether an email exists, and are limited per user and hour.
/// - Setting the password clears a lockout and signs the user out everywhere.
/// </summary>
public sealed class PasswordTokenService(
    TasDbContext db,
    PasswordService passwords,
    PayloadProtector protector,
    IOptions<PasswordTokenOptions> options,
    TimeProvider clock,
    ILogger<PasswordTokenService> logger)
{
    public const string InvalidLink = "This link is invalid, already used or expired. Ask for a new one.";

    /// <summary>Forgotten password. Does nothing (silently) for unknown or deactivated accounts, or too many requests.</summary>
    public async Task RequestResetAsync(string? email, string? ip, CancellationToken ct = default)
    {
        var normalized = (email ?? "").Trim().ToLowerInvariant();
        if (normalized.Length is < 3 or > 200)
            return;

        var user = await db.AppUsers.AsNoTracking().SingleOrDefaultAsync(u => u.Email == normalized && u.IsActive, ct);
        if (user is null)
            return;

        var now = clock.GetUtcNow();
        var recent = await db.PasswordTokens.CountAsync(
            t => t.UserId == user.UserId && t.Purpose == PasswordTokenPurpose.Reset && t.CreatedAt > now.AddHours(-1), ct);
        if (recent >= options.Value.MaxResetRequestsPerHour)
        {
            logger.LogWarning("Password reset for user {UserId} ignored: {Count} requests in the last hour (from {Ip})", user.UserId, recent, ip);
            return;
        }

        await IssueAsync(user, PasswordTokenPurpose.Reset, ip, ct);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Creates a link of the given kind (cancelling earlier ones of that kind) and queues the email.
    /// Added to the context; the caller saves.
    /// </summary>
    internal async Task IssueAsync(AppUser user, PasswordTokenPurpose purpose, string? ip, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        await db.PasswordTokens
            .Where(t => t.UserId == user.UserId && t.Purpose == purpose && t.UsedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.UsedAt, now), ct);

        var token = TokenService.NewRefreshToken();
        db.PasswordTokens.Add(new PasswordToken
        {
            UserId = user.UserId,
            Purpose = purpose,
            TokenHash = TokenService.HashRefreshToken(token),
            CreatedAt = now,
            ExpiresAt = now + options.Value.ValidFor(purpose),
            CreatedIp = ip is { Length: > 45 } ? ip[..45] : ip,
        });
        db.NotificationLogs.Add(new NotificationLog
        {
            UserId = user.UserId,
            Channel = NotificationChannel.Email,
            TemplateCode = purpose == PasswordTokenPurpose.Invite ? NotificationTemplates.AccountInvite : NotificationTemplates.PasswordReset,
            Language = user.PreferredLanguage.Trim(),
            Payload = protector.Protect(token),
            CreatedAt = now,
        });
    }

    /// <summary>The link's user and purpose, if it can still be used.</summary>
    public async Task<PasswordTokenInfo?> CheckAsync(string? token, CancellationToken ct = default)
    {
        var found = await FindUsableAsync(token, ct);
        return found is null ? null : new PasswordTokenInfo(found.Value.Token.Purpose, found.Value.User.Email, found.Value.User.FullName, found.Value.Token.ExpiresAt);
    }

    /// <summary>Sets the password with a link. A password that doesn't meet the policy leaves the link usable.</summary>
    public async Task<(PasswordTokenStatus Status, string? Error)> CompleteAsync(string? token, string? newPassword, string? ip, CancellationToken ct = default)
    {
        var found = await FindUsableAsync(token, ct);
        if (found is null)
            return (PasswordTokenStatus.Invalid, InvalidLink);
        var (link, readOnlyUser) = found.Value;

        var problem = PasswordPolicy.Check(newPassword, readOnlyUser.Email);
        if (problem is not null)
            return (PasswordTokenStatus.Invalid, problem);

        var now = clock.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Consume atomically: of two parallel requests with the same link only one wins.
        var consumed = await db.PasswordTokens
            .Where(t => t.PasswordTokenId == link.PasswordTokenId && t.UsedAt == null && t.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.UsedAt, now), ct);
        if (consumed == 0)
        {
            await tx.RollbackAsync(ct);
            return (PasswordTokenStatus.Invalid, InvalidLink);
        }

        var user = await db.AppUsers.SingleAsync(u => u.UserId == readOnlyUser.UserId, ct);
        user.PasswordHash = passwords.Hash(user, newPassword!);
        user.FailedLoginCount = 0;
        user.LockedUntil = null;
        AuditWriter.Add(db, user.TerminalId, "app_user", user.UserId, AuditAction.Update, null, null,
            link.Purpose == PasswordTokenPurpose.Invite ? "Password set from invitation" : $"Password reset by email link (from {ip ?? "unknown"})",
            user.UserId, now);
        await db.SaveChangesAsync(ct);

        await CancelOutstandingAsync(db, user.UserId, now, ct);
        await db.RefreshTokens
            .Where(t => t.UserId == user.UserId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
        await tx.CommitAsync(ct);

        logger.LogInformation("User {UserId} set a new password ({Purpose} link)", user.UserId, link.Purpose);
        return (PasswordTokenStatus.Ok, null);
    }

    /// <summary>Cancels every unused link of a user (their password was just set another way).</summary>
    internal static Task CancelOutstandingAsync(TasDbContext db, int userId, DateTimeOffset now, CancellationToken ct) =>
        db.PasswordTokens
            .Where(t => t.UserId == userId && t.UsedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.UsedAt, now), ct);

    private async Task<(PasswordToken Token, AppUser User)?> FindUsableAsync(string? token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 100)
            return null;

        var now = clock.GetUtcNow();
        var hash = TokenService.HashRefreshToken(token.Trim());
        var row = await (
                from t in db.PasswordTokens.AsNoTracking()
                join u in db.AppUsers.AsNoTracking() on t.UserId equals u.UserId
                where t.TokenHash == hash && t.UsedAt == null && t.ExpiresAt > now && u.IsActive
                select new { t, u })
            .SingleOrDefaultAsync(ct);
        return row is null ? null : (row.t, row.u);
    }
}
