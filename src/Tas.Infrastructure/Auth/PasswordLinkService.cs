using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tas.Application.Auth;
using Tas.Application.Notifications;
using Tas.Domain;
using Tas.Domain.Parties;
using Tas.Infrastructure.Notifications;
using Tas.Infrastructure.Persistence;

namespace Tas.Infrastructure.Auth;

/// <summary>Configuration section "PasswordLinks".</summary>
public sealed class PasswordLinkOptions
{
    /// <summary>
    /// The portal's address for the links in the emails, e.g. https://tas.example.com. Required on a real server: the
    /// address a request came to is set by the caller, so a link built from it could point to someone else's site.
    /// Empty on a developer's machine or in tests: the address the request came to.
    /// </summary>
    public string PortalUrl { get; set; } = "";

    public int ResetMinutes { get; set; } = 60;
    public int InviteDays { get; set; } = 7;

    /// <summary>"Forgot password" emails per account per hour.</summary>
    public int MaxResetsPerHour { get; set; } = 3;

    /// <summary>Tests only: return the link's token in the response. Never enable in production.</summary>
    public bool ExposeTokenForTesting { get; set; }
}

public static class PasswordTokenPurpose
{
    public const string Reset = "RESET";
    public const string Invite = "INVITE";
}

/// <param name="Purpose">RESET or INVITE.</param>
public sealed record PasswordLinkInfo(string Purpose, string FullName, string Email, string PreferredLanguage);

/// <summary>
/// Links by email to set a password: "forgot password" for anyone (the answer is the same whether or not the email
/// has an account) and invitations for new users, so administrators never have to know or send passwords.
/// A link works once; setting the password ends every session of the account and lifts a lock-out.
/// </summary>
public sealed class PasswordLinkService(
    TasDbContext db,
    PasswordService passwords,
    NotificationSenders senders,
    IOptions<PasswordLinkOptions> options,
    TimeProvider clock,
    Microsoft.Extensions.Hosting.IHostEnvironment env,
    ILogger<PasswordLinkService> logger,
    TerminalProfiles? profiles = null)
{
    public const string BadLink = "This link is not valid any more. Ask for a new one.";

    /// <summary>Sends a reset link if the email belongs to an active account; returns the token only in tests.</summary>
    public async Task<string?> RequestResetAsync(string? email, string requestBaseUrl, CancellationToken ct = default)
    {
        var normalized = (email ?? "").Trim().ToLowerInvariant();
        var user = normalized.Length is > 2 and <= 200
            ? await db.AppUsers.SingleOrDefaultAsync(u => u.Email == normalized && u.IsActive, ct)
            : null;
        if (user is null)
            return null;

        var now = clock.GetUtcNow();
        var recent = await db.PasswordTokens.CountAsync(t => t.UserId == user.UserId && t.Purpose == PasswordTokenPurpose.Reset && t.CreatedAt > now.AddHours(-1), ct);
        if (recent >= options.Value.MaxResetsPerHour)
        {
            logger.LogWarning("Password reset for user {UserId} refused: {Count} links in the last hour", user.UserId, recent);
            return null;
        }

        var token = await IssueAsync(user, PasswordTokenPurpose.Reset, now.AddMinutes(options.Value.ResetMinutes), null, ct);
        await SendAsync(user, PasswordTokenPurpose.Reset, Link(requestBaseUrl, token), ct);
        return options.Value.ExposeTokenForTesting ? token : null;
    }

    /// <summary>Emails an invitation (no password yet) or a reset link (has one) on behalf of an administrator.</summary>
    public async Task<string?> InviteAsync(AppUser user, int actorUserId, string requestBaseUrl, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var purpose = user.PasswordHash is null ? PasswordTokenPurpose.Invite : PasswordTokenPurpose.Reset;
        var expires = purpose == PasswordTokenPurpose.Invite ? now.AddDays(options.Value.InviteDays) : now.AddMinutes(options.Value.ResetMinutes);
        var token = await IssueAsync(user, purpose, expires, actorUserId, ct);
        await SendAsync(user, purpose, Link(requestBaseUrl, token), ct);
        return options.Value.ExposeTokenForTesting ? token : null;
    }

    /// <summary>Who the link is for (shown on the "set your password" page); null when it doesn't work.</summary>
    public async Task<PasswordLinkInfo?> InfoAsync(string? token, CancellationToken ct = default)
    {
        var row = await ValidAsync(token, ct);
        if (row is null)
            return null;
        var user = await db.AppUsers.AsNoTracking().SingleAsync(u => u.UserId == row.UserId, ct);
        return new PasswordLinkInfo(row.Purpose, user.FullName, user.Email, user.PreferredLanguage.Trim());
    }

    /// <summary>Sets the password from a link. Returns an error, or null when done.</summary>
    public async Task<string?> CompleteAsync(string? token, string? newPassword, CancellationToken ct = default)
    {
        var row = await ValidAsync(token, ct);
        if (row is null)
            return BadLink;
        var user = await db.AppUsers.SingleAsync(u => u.UserId == row.UserId, ct);
        var problem = PasswordPolicy.Check(newPassword ?? "", user.Email);
        if (problem is not null)
            return problem;

        var now = clock.GetUtcNow();
        // Use the link at most once, also when two tabs submit at the same time.
        var claimed = await db.PasswordTokens.Where(t => t.TokenId == row.TokenId && t.UsedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.UsedAt, now), ct);
        if (claimed == 0)
            return BadLink;

        user.PasswordHash = passwords.Hash(user, newPassword!);
        user.FailedLoginCount = 0;
        user.LockedUntil = null;
        AuditWriter.Add(db, user.TerminalId, "app_user", user.UserId, AuditAction.Update, null, null,
            row.Purpose == PasswordTokenPurpose.Invite ? "Password set from invitation" : "Password reset by email link", user.UserId, now);
        await db.SaveChangesAsync(ct);

        // Every other open link and every session of the account end.
        await db.PasswordTokens.Where(t => t.UserId == user.UserId && t.UsedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.UsedAt, now), ct);
        await db.RefreshTokens.Where(t => t.UserId == user.UserId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
        return null;
    }

    private async Task<PasswordToken?> ValidAsync(string? token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 100)
            return null;
        var hash = Hash(token.Trim());
        var now = clock.GetUtcNow();
        return await db.PasswordTokens.AsNoTracking()
            .Where(t => t.TokenHash == hash && t.UsedAt == null && t.ExpiresAt > now
                        && db.AppUsers.Any(u => u.UserId == t.UserId && u.IsActive))
            .SingleOrDefaultAsync(ct);
    }

    private async Task<string> IssueAsync(AppUser user, string purpose, DateTimeOffset expires, int? createdBy, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        // A new link replaces older unused ones of the same kind.
        await db.PasswordTokens.Where(t => t.UserId == user.UserId && t.Purpose == purpose && t.UsedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.UsedAt, now), ct);

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        db.PasswordTokens.Add(new PasswordToken
        {
            UserId = user.UserId,
            Purpose = purpose,
            TokenHash = Hash(token),
            CreatedAt = now,
            ExpiresAt = expires,
            CreatedBy = createdBy,
        });
        await db.SaveChangesAsync(ct);
        return token;
    }

    /// <summary>The link, or null when the portal's address isn't configured on a real server.</summary>
    private string? Link(string requestBaseUrl, string token)
    {
        var portal = !string.IsNullOrWhiteSpace(options.Value.PortalUrl) ? options.Value.PortalUrl
            : Ops.SecurityChecks.IsTestHost(env) ? requestBaseUrl
            : null;
        return portal is null ? null : $"{portal.TrimEnd('/')}/#/password?token={token}";
    }

    private async Task SendAsync(AppUser user, string purpose, string? link, CancellationToken ct)
    {
        if (link is null)
        {
            logger.LogError("PasswordLinks:PortalUrl is not set; password link for user {UserId} not sent", user.UserId);
            return;
        }
        var sender = senders.For(NotificationChannel.Email);
        if (sender is null)
        {
            logger.LogWarning("No email sender configured; password link for user {UserId} not sent", user.UserId);
            return;
        }
        try
        {
            // Terminal staff get their terminal's name and sender; everyone else the installation's.
            var code = user.TerminalId is { } terminalId ? await TerminalProfiles.CodeAsync(db, terminalId, ct) : null;
            var brand = profiles?.BrandFor(code) ?? TerminalProfiles.DefaultBrand;
            var message = Message(purpose, user.PreferredLanguage.Trim(), user.FullName, link,
                purpose == PasswordTokenPurpose.Invite ? options.Value.InviteDays : options.Value.ResetMinutes);
            if (brand != TerminalProfiles.DefaultBrand)
                message = new RenderedMessage(message.Subject.Replace(TerminalProfiles.DefaultBrand, brand), message.Body.Replace(TerminalProfiles.DefaultBrand, brand));
            await (senders.For(NotificationChannel.Email, code) ?? sender).SendAsync(user.Email, message, ct);
        }
        catch (NotificationSendException ex)
        {
            logger.LogWarning(ex, "Password link for user {UserId} could not be sent", user.UserId);
        }
    }

    /// <summary>The email, in Arabic or English (the portal's languages).</summary>
    public static RenderedMessage Message(string purpose, string language, string name, string link, int validFor)
    {
        var ar = language == "ar";
        return (purpose, ar) switch
        {
            (PasswordTokenPurpose.Invite, false) => new("Yardsyncs — your account is ready",
                $"Hello {name},\n\nAn account was created for you on Yardsyncs. Set your password here (the link works once, for {validFor} days):\n\n{link}\n\nIf you did not expect this, ignore this email."),
            (PasswordTokenPurpose.Invite, true) => new("Yardsyncs — حسابك جاهز",
                $"مرحباً {name}،\n\nتم إنشاء حساب لك على Yardsyncs. عيّن كلمة المرور من هنا (يعمل الرابط مرة واحدة لمدة {validFor} أيام):\n\n{link}\n\nإذا لم تكن تتوقع هذه الرسالة فتجاهلها."),
            (_, false) => new("Yardsyncs — set a new password",
                $"Hello {name},\n\nSomeone asked to reset the password of your Yardsyncs account. Set a new password here (the link works once, for {validFor} minutes):\n\n{link}\n\nIf it wasn't you, ignore this email: your password stays as it is."),
            _ => new("Yardsyncs — تعيين كلمة مرور جديدة",
                $"مرحباً {name}،\n\nطُلب تغيير كلمة مرور حسابك على Yardsyncs. عيّن كلمة مرور جديدة من هنا (يعمل الرابط مرة واحدة لمدة {validFor} دقيقة):\n\n{link}\n\nإذا لم تكن أنت صاحب الطلب فتجاهل الرسالة: تبقى كلمة المرور كما هي."),
        };
    }

    private static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));
}
