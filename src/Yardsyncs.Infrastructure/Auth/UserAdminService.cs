using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using Yardsyncs.Application.Auth;
using Yardsyncs.Application.Capacity;
using Yardsyncs.Application.MasterData;
using Yardsyncs.Domain;
using Yardsyncs.Domain.Parties;
using Yardsyncs.Infrastructure.Persistence;

namespace Yardsyncs.Infrastructure.Auth;

public sealed record CreateUserRequest(
    string Email,
    string FullName,
    UserRole Role,
    int? CompanyId,
    int? TerminalId,
    string? Password = null,
    string? Mobile = null,
    string PreferredLanguage = "en",
    int? OperatorId = null);

public enum UserAdminStatus
{
    Created,
    Forbidden,
    Invalid,
    Conflict,
}

/// <param name="TestToken">The invitation link's token, only when PasswordLinks:ExposeTokenForTesting is on.</param>
public sealed record UserAdminResult(UserAdminStatus Status, UserProfile? User = null, string? Error = null, string? TestToken = null);

/// <summary>Creates users within the rules of <see cref="AccessRules.CanCreateUser"/>.</summary>
public sealed class UserAdminService(YardsyncsDbContext db, PasswordService passwords, AuthService auth, TimeProvider clock, PasswordLinkService? links = null)
{

    /// <summary>
    /// Creates a user. Without a password the user gets an invitation by email to set one
    /// (<paramref name="linkBaseUrl"/>: the portal address for the link when PasswordLinks:PortalUrl isn't set).
    /// </summary>
    public async Task<UserAdminResult> CreateAsync(CurrentUser actor, CreateUserRequest request, CancellationToken ct = default, string linkBaseUrl = "")
    {
        if (!AccessRules.CanCreateUser(actor, request.Role, request.CompanyId, request.TerminalId, request.OperatorId))
            return new UserAdminResult(UserAdminStatus.Forbidden, Error: "You cannot create this kind of user here.");

        var email = (request.Email ?? "").Trim().ToLowerInvariant();
        if (email.Length is < 3 or > 200 || !email.Contains('@'))
            return new UserAdminResult(UserAdminStatus.Invalid, Error: "A valid email is required.");
        if (string.IsNullOrWhiteSpace(request.FullName) || request.FullName.Length > 150)
            return new UserAdminResult(UserAdminStatus.Invalid, Error: "Full name is required (max 150 characters).");

        var language = (request.PreferredLanguage ?? "en").Trim().ToLowerInvariant();
        if (!MasterDataValidator.UserLanguages.Contains(language))
            return new UserAdminResult(UserAdminStatus.Invalid, Error: $"Language must be one of: {string.Join(", ", MasterDataValidator.UserLanguages)}.");

        var invite = string.IsNullOrEmpty(request.Password);
        if (invite && links is null)
            return new UserAdminResult(UserAdminStatus.Invalid, Error: "A password is required.");
        var problem = invite ? null : PasswordPolicy.Check(request.Password!, email);
        if (problem is not null)
            return new UserAdminResult(UserAdminStatus.Invalid, Error: problem);

        if (request.CompanyId is { } companyId && !await db.Companies.AnyAsync(c => c.CompanyId == companyId, ct))
            return new UserAdminResult(UserAdminStatus.Invalid, Error: $"Company {companyId} does not exist.");
        if (request.TerminalId is { } terminalId && !await db.Terminals.AnyAsync(t => t.TerminalId == terminalId, ct))
            return new UserAdminResult(UserAdminStatus.Invalid, Error: $"Terminal {terminalId} does not exist.");
        if (request.OperatorId is { } operatorId && !await db.TerminalOperators.AnyAsync(o => o.OperatorId == operatorId, ct))
            return new UserAdminResult(UserAdminStatus.Invalid, Error: $"Operator {operatorId} does not exist.");

        if (await db.AppUsers.AnyAsync(u => u.Email == email, ct))
            return new UserAdminResult(UserAdminStatus.Conflict, Error: "A user with this email already exists.");

        var user = new AppUser
        {
            Email = email,
            FullName = request.FullName.Trim(),
            Role = request.Role,
            CompanyId = request.CompanyId,
            TerminalId = request.TerminalId,
            OperatorId = request.OperatorId,
            Mobile = string.IsNullOrWhiteSpace(request.Mobile) ? null : request.Mobile.Trim(),
            PreferredLanguage = language,
        };
        user.PasswordHash = invite ? null : passwords.Hash(user, request.Password!);
        db.AppUsers.Add(user);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return new UserAdminResult(UserAdminStatus.Conflict, Error: "A user with this email already exists.");
        }

        Audit(user, AuditAction.Insert, null, UserView.From(user), invite ? "Invited by email" : null, actor);
        await db.SaveChangesAsync(ct);
        var testToken = invite ? await links!.InviteAsync(user, actor.UserId, linkBaseUrl, ct) : null;
        return new UserAdminResult(UserAdminStatus.Created, await auth.ProfileAsync(user, ct), TestToken: testToken);
    }

    /// <summary>
    /// Users the actor manages: CompanyAdmin — own company; TerminalAdmin — own terminal's staff and all
    /// company users; OperatorAdmin — the operator's admins, its terminals' staff and all company users; SysAdmin — everyone. Optional filters narrow it further.
    /// </summary>
    public async Task<AdminResult<IReadOnlyList<UserView>>> ListAsync(
        CurrentUser actor, int? companyId, int? terminalId, CancellationToken ct = default, int? operatorId = null)
    {
        var query = db.AppUsers.AsNoTracking();
        switch (actor.Role)
        {
            case UserRole.SysAdmin:
                break;
            case UserRole.TerminalAdmin:
                query = query.Where(u => u.TerminalId == actor.TerminalId || u.CompanyId != null);
                break;
            case UserRole.OperatorAdmin:
                var terminals = actor.OperatorTerminals ?? [];
                query = query.Where(u => (u.TerminalId != null && terminals.Contains(u.TerminalId.Value))
                                         || (u.OperatorId != null && u.OperatorId == actor.OperatorId)
                                         || u.CompanyId != null);
                break;
            case UserRole.CompanyAdmin:
                query = query.Where(u => u.CompanyId == actor.CompanyId);
                break;
            default:
                return AdminResult<IReadOnlyList<UserView>>.Forbidden("You cannot list users.");
        }

        if (companyId is not null)
            query = query.Where(u => u.CompanyId == companyId);
        if (terminalId is not null)
            query = query.Where(u => u.TerminalId == terminalId);
        if (operatorId is not null)
            query = query.Where(u => u.OperatorId == operatorId);

        var users = await query.OrderBy(u => u.Email).Take(1000).ToListAsync(ct);
        return AdminResult<IReadOnlyList<UserView>>.Ok(users.Select(UserView.From).ToList());
    }

    /// <summary>Changes name, mobile, language and role (within the actor's scope). Nobody changes their own role.</summary>
    public async Task<AdminResult<UserView>> UpdateAsync(CurrentUser actor, int userId, UpdateUserInput input, CancellationToken ct = default)
    {
        var (user, denied) = await LoadManagedAsync(actor, userId, ct);
        if (denied is not null)
            return denied;

        var errors = MasterDataValidator.User(input);
        if (errors.Count > 0)
            return AdminResult<UserView>.Invalid(errors);
        if (input.Role != user!.Role)
        {
            if (user.UserId == actor.UserId)
                return AdminResult<UserView>.Invalid("You cannot change your own role.");
            if (!AccessRules.CanCreateUser(actor, input.Role, user.CompanyId, user.TerminalId, user.OperatorId))
                return AdminResult<UserView>.Forbidden($"You cannot give this user the role {input.Role}.");
        }

        var before = UserView.From(user);
        user.FullName = input.FullName.Trim();
        user.Mobile = string.IsNullOrWhiteSpace(input.Mobile) ? null : PhoneNumber.Normalize(input.Mobile);
        user.PreferredLanguage = input.PreferredLanguage.Trim().ToLowerInvariant();
        user.Role = input.Role;
        Audit(user, AuditAction.Update, before, UserView.From(user), null, actor);
        await db.SaveChangesAsync(ct);
        return AdminResult<UserView>.Ok(UserView.From(user));
    }

    /// <summary>Deactivating signs the user out everywhere; their tokens stop working on the next request.</summary>
    public async Task<AdminResult<UserView>> SetActiveAsync(
        CurrentUser actor, int userId, bool active, string? reason, CancellationToken ct = default)
    {
        var (user, denied) = await LoadManagedAsync(actor, userId, ct);
        if (denied is not null)
            return denied;
        if (user!.UserId == actor.UserId)
            return AdminResult<UserView>.Invalid("You cannot deactivate your own account.");
        if (!active && string.IsNullOrWhiteSpace(reason))
            return AdminResult<UserView>.Invalid("A reason is required to deactivate a user.");

        if (user.IsActive != active)
        {
            var before = UserView.From(user);
            user.IsActive = active;
            if (active)
            {
                user.FailedLoginCount = 0;
                user.LockedUntil = null;
            }
            Audit(user, AuditAction.Update, before, UserView.From(user), reason, actor);
            await db.SaveChangesAsync(ct);
            if (!active)
                await RevokeSessionsAsync(user.UserId, ct);
        }
        return AdminResult<UserView>.Ok(UserView.From(user));
    }

    /// <summary>
    /// Emails the user a link to set their password: an invitation when they have none yet, else a reset link.
    /// Returns the link's token only in tests.
    /// </summary>
    public async Task<AdminResult<string?>> SendPasswordLinkAsync(CurrentUser actor, int userId, string linkBaseUrl, CancellationToken ct = default)
    {
        var (user, denied) = await LoadManagedAsync(actor, userId, ct);
        if (denied is not null)
            return denied.Status == AdminStatus.NotFound ? AdminResult<string?>.NotFound(string.Join(" ", denied.Errors)) : AdminResult<string?>.Invalid(denied.Errors);
        if (!user!.IsActive)
            return AdminResult<string?>.Invalid("The user is deactivated; activate them first.");
        if (links is null)
            return AdminResult<string?>.Invalid("Email links are not available.");
        return AdminResult<string?>.Ok(await links.InviteAsync(user, actor.UserId, linkBaseUrl, ct));
    }

    /// <summary>Sets a new password for someone else (use change-password for yourself) and ends their sessions.</summary>
    public async Task<AdminResult<UserView>> ResetPasswordAsync(
        CurrentUser actor, int userId, string newPassword, CancellationToken ct = default)
    {
        var (user, denied) = await LoadManagedAsync(actor, userId, ct);
        if (denied is not null)
            return denied;
        if (user!.UserId == actor.UserId)
            return AdminResult<UserView>.Invalid("Use change-password for your own account.");

        var problem = PasswordPolicy.Check(newPassword, user.Email);
        if (problem is not null)
            return AdminResult<UserView>.Invalid(problem);

        user.PasswordHash = passwords.Hash(user, newPassword);
        user.FailedLoginCount = 0;
        user.LockedUntil = null;
        Audit(user, AuditAction.Update, null, null, "Password reset by administrator", actor);
        await db.SaveChangesAsync(ct);
        await RevokeSessionsAsync(user.UserId, ct);
        return AdminResult<UserView>.Ok(UserView.From(user));
    }

    private async Task<(AppUser? User, AdminResult<UserView>? Denied)> LoadManagedAsync(CurrentUser actor, int userId, CancellationToken ct)
    {
        var user = await db.AppUsers.SingleOrDefaultAsync(u => u.UserId == userId, ct);
        if (user is null)
            return (null, AdminResult<UserView>.NotFound($"User {userId} not found."));

        // Managing an existing user needs the same rights as creating one like it — except editing yourself.
        if (user.UserId != actor.UserId && !AccessRules.CanCreateUser(actor, user.Role, user.CompanyId, user.TerminalId, user.OperatorId))
            return (null, AdminResult<UserView>.NotFound($"User {userId} not found."));

        return (user, null);
    }

    private Task RevokeSessionsAsync(int userId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        return db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
    }

    private void Audit(AppUser user, AuditAction action, object? before, object? after, string? reason, CurrentUser actor) =>
        AuditWriter.Add(db, user.TerminalId, "app_user", user.UserId, action, before, after, reason, actor.UserId, clock.GetUtcNow());
}

/// <summary>
/// Creates the first SysAdmin from configuration (Bootstrap:AdminEmail / Bootstrap:AdminPassword)
/// when no SysAdmin exists yet. Remove the settings after the first start.
/// </summary>
public sealed class AdminBootstrapper(YardsyncsDbContext db, PasswordService passwords, IConfiguration config, ILogger<AdminBootstrapper> logger)
{
    public async Task EnsureAsync(CancellationToken ct = default)
    {
        var email = config["Bootstrap:AdminEmail"]?.Trim().ToLowerInvariant();
        var password = config["Bootstrap:AdminPassword"];
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            return;

        if (await db.AppUsers.AnyAsync(u => u.Role == UserRole.SysAdmin, ct))
            return;

        var problem = PasswordPolicy.Check(password, email);
        if (problem is not null)
            throw new InvalidOperationException($"Bootstrap:AdminPassword is not acceptable: {problem}");

        var admin = new AppUser { Email = email, FullName = "System Administrator", Role = UserRole.SysAdmin };
        admin.PasswordHash = passwords.Hash(admin, password);
        db.AppUsers.Add(admin);
        await db.SaveChangesAsync(ct);
        logger.LogWarning("Created bootstrap SysAdmin {Email}. Remove Bootstrap:* settings now.", email);
    }
}
