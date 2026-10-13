using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using Tas.Application.Auth;
using Tas.Domain.Parties;
using Tas.Infrastructure.Persistence;

namespace Tas.Infrastructure.Auth;

public sealed record CreateUserRequest(
    string Email,
    string FullName,
    UserRole Role,
    int? CompanyId,
    int? TerminalId,
    string Password,
    string? Mobile = null,
    string PreferredLanguage = "en");

public enum UserAdminStatus
{
    Created,
    Forbidden,
    Invalid,
    Conflict,
}

public sealed record UserAdminResult(UserAdminStatus Status, UserProfile? User = null, string? Error = null);

/// <summary>Creates users within the rules of <see cref="AccessRules.CanCreateUser"/>.</summary>
public sealed class UserAdminService(TasDbContext db, PasswordService passwords, AuthService auth)
{
    private static readonly HashSet<string> Languages = ["ar", "en", "ur", "hi"];

    public async Task<UserAdminResult> CreateAsync(CurrentUser actor, CreateUserRequest request, CancellationToken ct = default)
    {
        if (!AccessRules.CanCreateUser(actor, request.Role, request.CompanyId, request.TerminalId))
            return new UserAdminResult(UserAdminStatus.Forbidden, Error: "You cannot create this kind of user here.");

        var email = (request.Email ?? "").Trim().ToLowerInvariant();
        if (email.Length is < 3 or > 200 || !email.Contains('@'))
            return new UserAdminResult(UserAdminStatus.Invalid, Error: "A valid email is required.");
        if (string.IsNullOrWhiteSpace(request.FullName) || request.FullName.Length > 150)
            return new UserAdminResult(UserAdminStatus.Invalid, Error: "Full name is required (max 150 characters).");

        var language = (request.PreferredLanguage ?? "en").Trim().ToLowerInvariant();
        if (!Languages.Contains(language))
            return new UserAdminResult(UserAdminStatus.Invalid, Error: "Language must be one of: ar, en, ur, hi.");

        var problem = PasswordPolicy.Check(request.Password, email);
        if (problem is not null)
            return new UserAdminResult(UserAdminStatus.Invalid, Error: problem);

        if (request.CompanyId is { } companyId && !await db.Companies.AnyAsync(c => c.CompanyId == companyId, ct))
            return new UserAdminResult(UserAdminStatus.Invalid, Error: $"Company {companyId} does not exist.");
        if (request.TerminalId is { } terminalId && !await db.Terminals.AnyAsync(t => t.TerminalId == terminalId, ct))
            return new UserAdminResult(UserAdminStatus.Invalid, Error: $"Terminal {terminalId} does not exist.");

        if (await db.AppUsers.AnyAsync(u => u.Email == email, ct))
            return new UserAdminResult(UserAdminStatus.Conflict, Error: "A user with this email already exists.");

        var user = new AppUser
        {
            Email = email,
            FullName = request.FullName.Trim(),
            Role = request.Role,
            CompanyId = request.CompanyId,
            TerminalId = request.TerminalId,
            Mobile = string.IsNullOrWhiteSpace(request.Mobile) ? null : request.Mobile.Trim(),
            PreferredLanguage = language,
        };
        user.PasswordHash = passwords.Hash(user, request.Password);
        db.AppUsers.Add(user);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return new UserAdminResult(UserAdminStatus.Conflict, Error: "A user with this email already exists.");
        }

        return new UserAdminResult(UserAdminStatus.Created, await auth.ProfileAsync(user, ct));
    }
}

/// <summary>
/// Creates the first SysAdmin from configuration (Bootstrap:AdminEmail / Bootstrap:AdminPassword)
/// when no SysAdmin exists yet. Remove the settings after the first start.
/// </summary>
public sealed class AdminBootstrapper(TasDbContext db, PasswordService passwords, IConfiguration config, ILogger<AdminBootstrapper> logger)
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
