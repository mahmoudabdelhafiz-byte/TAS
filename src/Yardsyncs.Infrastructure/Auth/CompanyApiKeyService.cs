using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yardsyncs.Application.Auth;
using Yardsyncs.Application.Capacity;
using Yardsyncs.Domain;
using Yardsyncs.Domain.Parties;
using Yardsyncs.Infrastructure.Persistence;

namespace Yardsyncs.Infrastructure.Auth;

/// <summary>
/// Keys for a trucking company's own system to use the API (book from their TMS). Each key acts as its own company user
/// account, which has no password and can't sign in; revoking the key switches the account off. Made by the company's
/// admin (or terminal admins, as for the fleet).
/// </summary>
public sealed class CompanyApiKeyService(YardsyncsDbContext db, TimeProvider clock, ILogger<CompanyApiKeyService> logger)
{
    public async Task<IReadOnlyList<CompanyApiKeyView>> ListAsync(int companyId, CancellationToken ct = default) =>
        (await db.CompanyApiKeys.AsNoTracking().Where(k => k.CompanyId == companyId)
            .OrderBy(k => k.RevokedAt != null).ThenBy(k => k.Name).ToListAsync(ct))
        .Select(View).ToList();

    public async Task<AdminResult<CompanyApiKeyIssued>> CreateAsync(CurrentUser admin, int companyId, CompanyApiKeyInput input, CancellationToken ct = default)
    {
        if (CompanyApiKeys.Validate(input) is { Count: > 0 } errors)
            return AdminResult<CompanyApiKeyIssued>.Invalid(errors);
        var company = await db.Companies.AsNoTracking().Where(c => c.CompanyId == companyId).Select(c => new { c.CompanyId }).SingleOrDefaultAsync(ct);
        if (company is null)
            return AdminResult<CompanyApiKeyIssued>.NotFound($"Company {companyId} not found.");
        var name = input.Name.Trim();
        var active = await db.CompanyApiKeys.Where(k => k.CompanyId == companyId && k.RevokedAt == null).Select(k => k.Name).ToListAsync(ct);
        if (active.Contains(name))
            return AdminResult<CompanyApiKeyIssued>.Conflict($"There is already a key called \"{name}\".");
        if (active.Count >= CompanyApiKeys.MaxActive)
            return AdminResult<CompanyApiKeyIssued>.Conflict($"At most {CompanyApiKeys.MaxActive} keys at a time: revoke one you no longer use.");

        var now = clock.GetUtcNow();
        var key = CompanyApiKeys.New();
        var account = new AppUser
        {
            CompanyId = companyId,
            // Not a mailbox: .invalid can never receive mail, so no password link can reach anyone.
            Email = $"api-{Guid.NewGuid():N}@company-{companyId}.api.invalid",
            FullName = $"API: {name}",
            Role = UserRole.CompanyUser,
            IsActive = true,
            CreatedAt = now,
        };
        db.AppUsers.Add(account);
        await db.SaveChangesAsync(ct);

        var row = new CompanyApiKey
        {
            CompanyId = companyId,
            UserId = account.UserId,
            Name = name,
            Scope = input.Scope,
            KeyHash = CompanyApiKeys.Hash(key),
            KeyHint = CompanyApiKeys.Hint(key),
            CreatedAt = now,
            CreatedBy = admin.UserId,
        };
        db.CompanyApiKeys.Add(row);
        await db.SaveChangesAsync(ct);
        AuditWriter.Add(db, null, "company_api_key", row.KeyId, AuditAction.Insert, null, View(row), "API key issued", admin.UserId, now);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("API key {KeyId} ({Name}, {Scope}) issued for company {CompanyId} by user {UserId}", row.KeyId, name, row.Scope, companyId, admin.UserId);
        return AdminResult<CompanyApiKeyIssued>.Created(new CompanyApiKeyIssued(View(row), key));
    }

    public async Task<AdminResult<CompanyApiKeyView>> RevokeAsync(CurrentUser admin, int companyId, int keyId, CancellationToken ct = default)
    {
        var row = await db.CompanyApiKeys.SingleOrDefaultAsync(k => k.CompanyId == companyId && k.KeyId == keyId, ct);
        if (row is null)
            return AdminResult<CompanyApiKeyView>.NotFound($"API key {keyId} not found.");
        if (row.RevokedAt is not null)
            return AdminResult<CompanyApiKeyView>.Ok(View(row));
        var now = clock.GetUtcNow();
        var before = View(row);
        row.RevokedAt = now;
        row.RevokedBy = admin.UserId;
        await db.AppUsers.Where(u => u.UserId == row.UserId).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false), ct);
        AuditWriter.Add(db, null, "company_api_key", row.KeyId, AuditAction.Update, before, View(row), "API key revoked", admin.UserId, now);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("API key {KeyId} of company {CompanyId} revoked by user {UserId}", keyId, companyId, admin.UserId);
        return AdminResult<CompanyApiKeyView>.Ok(View(row));
    }

    /// <summary>The account a key acts as and whether it may only read; null for an unknown or revoked key, or a suspended company.</summary>
    public async Task<(AppUser User, bool ReadOnly)?> AuthenticateAsync(string? key, CancellationToken ct = default)
    {
        if (!CompanyApiKeys.LooksLikeKey(key))
            return null;
        var hash = CompanyApiKeys.Hash(key!);
        var found = await db.CompanyApiKeys
            .Where(k => k.KeyHash == hash && k.RevokedAt == null
                        && db.Companies.Any(c => c.CompanyId == k.CompanyId && c.Status != CompanyStatus.Suspended))
            .Join(db.AppUsers.Where(u => u.IsActive && u.Role == UserRole.CompanyUser), k => k.UserId, u => u.UserId, (k, u) => new { Key = k, User = u })
            .SingleOrDefaultAsync(ct);
        if (found is null || found.User.CompanyId != found.Key.CompanyId)
            return null;

        var now = clock.GetUtcNow();
        if (found.Key.LastUsedAt is null || now - found.Key.LastUsedAt > TimeSpan.FromMinutes(1))
            await db.CompanyApiKeys.Where(k => k.KeyId == found.Key.KeyId).ExecuteUpdateAsync(s => s.SetProperty(k => k.LastUsedAt, now), ct);
        return (found.User, found.Key.Scope == ApiKeyScope.Read);
    }

    private static CompanyApiKeyView View(CompanyApiKey k) =>
        new(k.KeyId, k.CompanyId, k.Name, k.Scope, k.KeyHint, k.CreatedAt, k.LastUsedAt, k.RevokedAt);
}
