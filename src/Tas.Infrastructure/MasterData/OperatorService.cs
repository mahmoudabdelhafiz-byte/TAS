using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Tas.Application.Auth;
using Tas.Application.Capacity;
using Tas.Domain;
using Tas.Infrastructure.Persistence;

namespace Tas.Infrastructure.MasterData;

public sealed record OperatorInput(string Code, string Name);

public sealed record OperatorUpdate(string Name, bool IsActive);

public sealed record OperatorTerminal(int TerminalId, string Code, string Name);

public sealed record OperatorView(int OperatorId, string Code, string Name, bool IsActive, IReadOnlyList<OperatorTerminal> Terminals, int Admins);

/// <summary>
/// Terminal operators: a group of terminals with its own administrators (OperatorAdmin). Only system admins set them up
/// and move terminals between them; an operator admin sees their own operator.
/// </summary>
public sealed partial class OperatorService(TasDbContext db, TimeProvider clock)
{
    [GeneratedRegex("^[A-Z0-9_-]{2,20}$")]
    private static partial Regex CodePattern();

    public async Task<AdminResult<IReadOnlyList<OperatorView>>> ListAsync(CurrentUser user, CancellationToken ct = default)
    {
        var query = db.TerminalOperators.AsNoTracking();
        if (user.IsOperatorAdmin)
            query = query.Where(o => o.OperatorId == user.OperatorId);
        else if (!user.IsSysAdmin)
            return AdminResult<IReadOnlyList<OperatorView>>.Forbidden("Only system admins manage operators.");

        var operators = await query.OrderBy(o => o.Code).ToListAsync(ct);
        var ids = operators.Select(o => o.OperatorId).ToList();
        var terminals = await db.Terminals.AsNoTracking()
            .Where(t => t.OperatorId != null && ids.Contains(t.OperatorId.Value))
            .OrderBy(t => t.Code)
            .Select(t => new { OperatorId = t.OperatorId!.Value, t.TerminalId, t.Code, t.Name })
            .ToListAsync(ct);
        var admins = await db.AppUsers.AsNoTracking()
            .Where(u => u.OperatorId != null && ids.Contains(u.OperatorId.Value) && u.IsActive)
            .GroupBy(u => u.OperatorId!.Value)
            .Select(g => new { OperatorId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.OperatorId, x => x.Count, ct);

        return AdminResult<IReadOnlyList<OperatorView>>.Ok(operators.Select(o => new OperatorView(
            o.OperatorId, o.Code, o.Name, o.IsActive,
            terminals.Where(t => t.OperatorId == o.OperatorId).Select(t => new OperatorTerminal(t.TerminalId, t.Code, t.Name)).ToList(),
            admins.GetValueOrDefault(o.OperatorId))).ToList());
    }

    public async Task<AdminResult<OperatorView>> CreateAsync(CurrentUser user, OperatorInput input, CancellationToken ct = default)
    {
        if (!AccessRules.CanManageOperators(user))
            return AdminResult<OperatorView>.Forbidden("Only system admins manage operators.");

        var code = (input.Code ?? "").Trim().ToUpperInvariant();
        var name = (input.Name ?? "").Trim();
        var errors = new List<string>();
        if (!CodePattern().IsMatch(code))
            errors.Add("Code: 2 to 20 letters, digits, - or _.");
        if (name.Length is 0 or > 150)
            errors.Add("Name is required (max 150 characters).");
        if (errors.Count > 0)
            return AdminResult<OperatorView>.Invalid(errors);
        if (await db.TerminalOperators.AnyAsync(o => o.Code == code, ct))
            return AdminResult<OperatorView>.Conflict($"An operator with code {code} already exists.");

        var op = new TerminalOperator { Code = code, Name = name };
        db.TerminalOperators.Add(op);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return AdminResult<OperatorView>.Conflict($"An operator with code {code} already exists.");
        }

        AuditWriter.Add(db, null, "terminal_operator", op.OperatorId, AuditAction.Insert, null, new { op.Code, op.Name }, null, user.UserId, clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return AdminResult<OperatorView>.Created(new OperatorView(op.OperatorId, op.Code, op.Name, op.IsActive, [], 0));
    }

    /// <summary>Renames or stops an operator. A stopped operator's admins are signed out on their next request.</summary>
    public async Task<AdminResult<OperatorView>> UpdateAsync(CurrentUser user, int operatorId, OperatorUpdate input, CancellationToken ct = default)
    {
        if (!AccessRules.CanManageOperators(user))
            return AdminResult<OperatorView>.Forbidden("Only system admins manage operators.");
        var name = (input.Name ?? "").Trim();
        if (name.Length is 0 or > 150)
            return AdminResult<OperatorView>.Invalid("Name is required (max 150 characters).");

        var op = await db.TerminalOperators.SingleOrDefaultAsync(o => o.OperatorId == operatorId, ct);
        if (op is null)
            return AdminResult<OperatorView>.NotFound($"Operator {operatorId} not found.");

        var before = new { op.Name, op.IsActive };
        op.Name = name;
        op.IsActive = input.IsActive;
        AuditWriter.Add(db, null, "terminal_operator", op.OperatorId, AuditAction.Update, before, new { op.Name, op.IsActive }, null, user.UserId, clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return await OneAsync(user, operatorId, ct);
    }

    /// <summary>Puts a terminal in an operator, moves it to another, or takes it out (null).</summary>
    public async Task<AdminResult<OperatorView?>> AssignTerminalAsync(CurrentUser user, int terminalId, int? operatorId, CancellationToken ct = default)
    {
        if (!AccessRules.CanManageOperators(user))
            return AdminResult<OperatorView?>.Forbidden("Only system admins manage operators.");

        var terminal = await db.Terminals.SingleOrDefaultAsync(t => t.TerminalId == terminalId, ct);
        if (terminal is null)
            return AdminResult<OperatorView?>.NotFound($"Terminal {terminalId} not found.");
        if (operatorId is { } id && !await db.TerminalOperators.AnyAsync(o => o.OperatorId == id, ct))
            return AdminResult<OperatorView?>.Invalid($"Operator {id} does not exist.");

        if (terminal.OperatorId != operatorId)
        {
            var before = new { terminal.OperatorId };
            terminal.OperatorId = operatorId;
            AuditWriter.Add(db, terminal.TerminalId, "terminal", terminal.TerminalId, AuditAction.Update, before, new { terminal.OperatorId },
                operatorId is null ? "Taken out of its operator" : "Put in an operator", user.UserId, clock.GetUtcNow());
            await db.SaveChangesAsync(ct);
        }

        if (operatorId is null)
            return AdminResult<OperatorView?>.Ok(null);
        var view = await OneAsync(user, operatorId.Value, ct);
        return AdminResult<OperatorView?>.Ok(view.Value);
    }

    private async Task<AdminResult<OperatorView>> OneAsync(CurrentUser user, int operatorId, CancellationToken ct)
    {
        var all = await ListAsync(user, ct);
        var one = all.Value?.SingleOrDefault(o => o.OperatorId == operatorId);
        return one is null ? AdminResult<OperatorView>.NotFound($"Operator {operatorId} not found.") : AdminResult<OperatorView>.Ok(one);
    }
}
