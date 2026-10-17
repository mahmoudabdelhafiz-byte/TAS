using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yardsyncs.Application.Auth;
using Yardsyncs.Application.Capacity;
using Yardsyncs.Application.Gate;
using Yardsyncs.Domain;
using Yardsyncs.Domain.Gate;
using Yardsyncs.Domain.Parties;
using Yardsyncs.Infrastructure.Persistence;

namespace Yardsyncs.Infrastructure.Gate;

/// <summary>
/// Keys for the terminal's own gate system (OCR portal, kiosk, gate operating system) to use the gate API: what a
/// terminal needs to run the truck appointment system with a gate that isn't Yardsyncs Autogate. Each key acts as its
/// own gate clerk account, which has no password and can't sign in; revoking the key switches the account off.
/// </summary>
public sealed class GateSystemService(YardsyncsDbContext db, TimeProvider clock, ILogger<GateSystemService> logger)
{
    public async Task<IReadOnlyList<GateSystemView>> ListAsync(int terminalId, CancellationToken ct = default) =>
        (await db.GateSystemKeys.AsNoTracking().Where(k => k.TerminalId == terminalId)
            .OrderBy(k => k.RevokedAt != null).ThenBy(k => k.Name).ToListAsync(ct))
        .Select(View).ToList();

    /// <summary>Make a key. It is returned once; only its fingerprint is kept.</summary>
    public async Task<AdminResult<GateSystemKeyIssued>> CreateAsync(CurrentUser admin, int terminalId, GateSystemInput input, CancellationToken ct = default)
    {
        if (GateSystemKeys.Validate(input) is { Count: > 0 } errors)
            return AdminResult<GateSystemKeyIssued>.Invalid(errors);
        var terminal = await db.Terminals.AsNoTracking().Where(t => t.TerminalId == terminalId).Select(t => new { t.Code }).SingleOrDefaultAsync(ct);
        if (terminal is null)
            return AdminResult<GateSystemKeyIssued>.NotFound($"Terminal {terminalId} not found.");
        var name = input.Name.Trim();
        if (await db.GateSystemKeys.AnyAsync(k => k.TerminalId == terminalId && k.RevokedAt == null && k.Name == name, ct))
            return AdminResult<GateSystemKeyIssued>.Conflict($"There is already a gate system called \"{name}\".");

        var now = clock.GetUtcNow();
        var key = GateSystemKeys.New();
        var account = new AppUser
        {
            TerminalId = terminalId,
            // Not a mailbox: .invalid can never receive mail, so no password link can reach anyone.
            Email = $"gate-system-{Guid.NewGuid():N}@{terminal.Code.ToLowerInvariant()}.gate-system.invalid",
            FullName = $"Gate system: {name}",
            Role = UserRole.GateClerk,
            IsActive = true,
            CreatedAt = now,
        };
        db.AppUsers.Add(account);
        await db.SaveChangesAsync(ct);

        var system = new GateSystemKey
        {
            TerminalId = terminalId,
            UserId = account.UserId,
            Name = name,
            KeyHash = GateSystemKeys.Hash(key),
            KeyHint = GateSystemKeys.Hint(key),
            CreatedAt = now,
            CreatedBy = admin.UserId,
        };
        db.GateSystemKeys.Add(system);
        await db.SaveChangesAsync(ct);
        AuditWriter.Add(db, terminalId, "gate_system_key", system.KeyId, AuditAction.Insert, null, View(system), "Gate system key issued", admin.UserId, now);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Gate system key {KeyId} ({Name}) issued at terminal {TerminalId} by user {UserId}", system.KeyId, name, terminalId, admin.UserId);
        return AdminResult<GateSystemKeyIssued>.Created(new GateSystemKeyIssued(View(system), key));
    }

    /// <summary>Stop a key working, at once. The history it made stays.</summary>
    public async Task<AdminResult<GateSystemView>> RevokeAsync(CurrentUser admin, int terminalId, int keyId, CancellationToken ct = default)
    {
        var system = await db.GateSystemKeys.SingleOrDefaultAsync(k => k.TerminalId == terminalId && k.KeyId == keyId, ct);
        if (system is null)
            return AdminResult<GateSystemView>.NotFound($"Gate system {keyId} not found.");
        if (system.RevokedAt is not null)
            return AdminResult<GateSystemView>.Ok(View(system));

        var now = clock.GetUtcNow();
        var before = View(system);
        system.RevokedAt = now;
        system.RevokedBy = admin.UserId;
        await db.AppUsers.Where(u => u.UserId == system.UserId).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false), ct);
        AuditWriter.Add(db, terminalId, "gate_system_key", system.KeyId, AuditAction.Update, before, View(system), "Gate system key revoked", admin.UserId, now);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Gate system key {KeyId} revoked at terminal {TerminalId} by user {UserId}", keyId, terminalId, admin.UserId);
        return AdminResult<GateSystemView>.Ok(View(system));
    }

    /// <summary>The account a key acts as, or null (unknown, revoked, or its account switched off).</summary>
    public async Task<AppUser?> AuthenticateAsync(string? key, CancellationToken ct = default)
    {
        if (!GateSystemKeys.LooksLikeKey(key))
            return null;
        var hash = GateSystemKeys.Hash(key!);
        var found = await db.GateSystemKeys
            .Where(k => k.KeyHash == hash && k.RevokedAt == null)
            .Join(db.AppUsers.Where(u => u.IsActive && u.Role == UserRole.GateClerk), k => k.UserId, u => u.UserId, (k, u) => new { Key = k, User = u })
            .SingleOrDefaultAsync(ct);
        if (found is null || found.User.TerminalId != found.Key.TerminalId)
            return null;

        // Last used: at most once a minute, so a busy gate doesn't write on every call.
        var now = clock.GetUtcNow();
        if (found.Key.LastUsedAt is null || now - found.Key.LastUsedAt > TimeSpan.FromMinutes(1))
            await db.GateSystemKeys.Where(k => k.KeyId == found.Key.KeyId).ExecuteUpdateAsync(s => s.SetProperty(k => k.LastUsedAt, now), ct);
        return found.User;
    }

    private static GateSystemView View(GateSystemKey k) => new(k.KeyId, k.Name, k.KeyHint, k.CreatedAt, k.LastUsedAt, k.RevokedAt);
}
