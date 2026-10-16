using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Tas.Application.Auth;
using Tas.Application.Capacity;
using Tas.Application.Tos;
using Tas.Domain;
using Tas.Domain.Capacity;
using Tas.Domain.Gate;
using Tas.Domain.Parties;
using Tas.Infrastructure.Persistence;
using Tas.Infrastructure.Tos.N4;

namespace Tas.Infrastructure.Onboarding;

/// <param name="Starter">Add the usual move types, an in and an out lane, and the usual booking rules.</param>
/// <param name="OperatorId">System admins choose the operator (or none); an operator admin's terminals are always their operator's.</param>
public sealed record NewTerminalInput(
    string Code, string Name, TerminalType TerminalType, string? TimeZone = null, int SlotMinutes = 60, int BookingHorizonDays = 14,
    int? OperatorId = null, bool Starter = true);

public sealed record NewTerminalView(int TerminalId, string Code, string Name, int MoveTypes, int Lanes);

/// <summary>One step of setting up a terminal.</summary>
/// <param name="Key">moveTypes, weeklyPlan, slots, lanes, tos, staff, companies, map.</param>
/// <param name="Optional">Bookings work without it.</param>
/// <param name="Count">The step's number (move types, open slots, in-lanes, staff, companies, map areas).</param>
/// <param name="Count2">Out-lanes, for the lanes step.</param>
public sealed record SetupStep(string Key, bool Done, bool Optional, string Detail, int? Count = null, int? Count2 = null);

/// <param name="Adapter">The terminal's TOS adapter (Fake, N4…).</param>
/// <param name="TosConnected">For N4: whether its connection is set.</param>
public sealed record SetupStatus(int TerminalId, string Code, bool ReadyToBook, IReadOnlyList<SetupStep> Steps, string Adapter, bool TosConnected);

public sealed record TosTestResult(string Adapter, bool Reached, string Message, long Milliseconds);

/// <summary>
/// Bringing a terminal onto Yardsyncs: create it (system admins, or operator admins for their operator) with the
/// usual move types, lanes and booking rules, then follow the checklist until it is ready to take bookings.
/// </summary>
public sealed partial class OnboardingService(
    TasDbContext db, TerminalProfiles profiles, ITosAdapter tos, IOptionsMonitor<N4Options> n4, TimeProvider clock)
{
    [GeneratedRegex("^[A-Z0-9]{2,20}$")]
    private static partial Regex CodePattern();

    /// <summary>The move types most terminals start with; a multipurpose terminal also gets breakbulk delivery.</summary>
    public static IReadOnlyList<(string Code, string En, string Ar, CategoryMode Mode, UnitType Unit)> StandardMoveTypes(TerminalType type)
    {
        var list = new List<(string, string, string, CategoryMode, UnitType)>
        {
            ("IMP_PICKUP", "Import pickup", "استلام حاوية وارد", CategoryMode.Container, UnitType.Move),
            ("EXP_DROP", "Export drop-off", "تسليم حاوية صادر", CategoryMode.Container, UnitType.Move),
            ("MTY_RETURN", "Empty return", "إرجاع حاوية فارغة", CategoryMode.Container, UnitType.Move),
            ("MTY_PICKUP", "Empty pickup", "استلام حاوية فارغة", CategoryMode.Container, UnitType.Move),
        };
        if (type == TerminalType.Multipurpose)
            list.Add(("BB_DELIVERY", "Breakbulk delivery", "استلام بضائع عامة", CategoryMode.Cargo, UnitType.Trip));
        return list;
    }

    public static bool CanCreate(CurrentUser user) => user.IsSysAdmin || user.IsOperatorAdmin;

    public async Task<AdminResult<NewTerminalView>> CreateAsync(CurrentUser user, NewTerminalInput input, CancellationToken ct = default)
    {
        if (!CanCreate(user))
            return AdminResult<NewTerminalView>.Forbidden("Only system admins and operator admins can add terminals.");

        var code = (input.Code ?? "").Trim().ToUpperInvariant();
        var name = (input.Name ?? "").Trim();
        var timeZone = string.IsNullOrWhiteSpace(input.TimeZone) ? profiles.InstallationCountry.TimeZone : input.TimeZone.Trim();
        var operatorId = user.IsOperatorAdmin ? user.OperatorId : input.OperatorId;
        var errors = new List<string>();
        if (!CodePattern().IsMatch(code))
            errors.Add("Code: 2 to 20 letters or digits, e.g. JED1. It names the terminal in settings and appointment numbers.");
        if (name.Length is 0 or > 150)
            errors.Add("Name is required (max 150 characters).");
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(timeZone, out _))
            errors.Add($"Time zone '{timeZone}' is not known; use an IANA name such as Asia/Riyadh.");
        if (input.SlotMinutes is not (15 or 30 or 60 or 120))
            errors.Add("Slot length: 15, 30, 60 or 120 minutes.");
        if (input.BookingHorizonDays is < 1 or > 90)
            errors.Add("Booking horizon: 1 to 90 days.");
        if (operatorId is { } op && !await db.TerminalOperators.AnyAsync(o => o.OperatorId == op && o.IsActive, ct))
            errors.Add($"Operator {op} does not exist or is stopped.");
        if (errors.Count > 0)
            return AdminResult<NewTerminalView>.Invalid(errors);
        if (await db.Terminals.AnyAsync(t => t.Code == code, ct))
            return AdminResult<NewTerminalView>.Conflict($"A terminal with code {code} already exists.");

        var terminal = new Terminal
        {
            Code = code, Name = name, TerminalType = input.TerminalType, TimeZone = timeZone,
            SlotMinutes = input.SlotMinutes, BookingHorizonDays = input.BookingHorizonDays, OperatorId = operatorId,
        };
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.Terminals.Add(terminal);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return AdminResult<NewTerminalView>.Conflict($"A terminal with code {code} already exists.");
        }

        var moveTypes = 0;
        var lanes = 0;
        if (input.Starter)
        {
            moveTypes = AddStandardMoveTypes(terminal, new HashSet<string>());
            db.GateLanes.AddRange(
                new GateLane { TerminalId = terminal.TerminalId, GateCode = "MAIN", LaneCode = "IN-1", Direction = LaneDirection.In },
                new GateLane { TerminalId = terminal.TerminalId, GateCode = "MAIN", LaneCode = "OUT-1", Direction = LaneDirection.Out });
            lanes = 2;
            db.TerminalSettings.AddRange(
                new TerminalSetting { TerminalId = terminal.TerminalId, SettingKey = TerminalSettings.EarlyGraceKey, SettingValue = "30" },
                new TerminalSetting { TerminalId = terminal.TerminalId, SettingKey = TerminalSettings.LateGraceKey, SettingValue = "30" },
                new TerminalSetting { TerminalId = terminal.TerminalId, SettingKey = TerminalSettings.CancelCutoffKey, SettingValue = "60" });
        }
        AuditWriter.Add(db, terminal.TerminalId, "terminal", terminal.TerminalId, AuditAction.Insert, null,
            new { terminal.Code, terminal.Name, terminal.TerminalType, terminal.TimeZone, terminal.SlotMinutes, terminal.BookingHorizonDays, terminal.OperatorId, input.Starter },
            "Added with the onboarding checklist", user.UserId, clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        Brands.BrandingService.Forget();

        return AdminResult<NewTerminalView>.Created(new NewTerminalView(terminal.TerminalId, code, name, moveTypes, lanes));
    }

    /// <summary>Adds the usual move types the terminal doesn't have yet (by code).</summary>
    public async Task<AdminResult<int>> AddStandardMoveTypesAsync(CurrentUser user, int terminalId, CancellationToken ct = default)
    {
        if (!AccessRules.CanManageCategories(user, terminalId))
            return AdminResult<int>.Forbidden("Only admins of this terminal can add move types.");
        var terminal = await db.Terminals.SingleOrDefaultAsync(t => t.TerminalId == terminalId, ct);
        if (terminal is null)
            return AdminResult<int>.NotFound($"Terminal {terminalId} not found.");
        var existing = await db.TransactionCategories.Where(c => c.TerminalId == terminalId).Select(c => c.Code).ToListAsync(ct);
        var added = AddStandardMoveTypes(terminal, existing.ToHashSet(StringComparer.OrdinalIgnoreCase));
        if (added > 0)
        {
            AuditWriter.Add(db, terminalId, "transaction_category", terminalId, AuditAction.Insert, null, new { Added = added },
                "The usual move types, from the onboarding checklist", user.UserId, clock.GetUtcNow());
            await db.SaveChangesAsync(ct);
        }
        return AdminResult<int>.Ok(added);
    }

    private int AddStandardMoveTypes(Terminal terminal, IReadOnlySet<string> existing)
    {
        var order = 0;
        var added = 0;
        foreach (var (code, en, ar, mode, unit) in StandardMoveTypes(terminal.TerminalType))
        {
            order++;
            if (existing.Contains(code))
                continue;
            db.TransactionCategories.Add(new TransactionCategory
            {
                TerminalId = terminal.TerminalId, Code = code, NameEn = en, NameAr = ar, Mode = mode, UnitType = unit, SortOrder = order,
            });
            added++;
        }
        return added;
    }

    /// <summary>The checklist: what the terminal has, and what it still needs before trucks can book.</summary>
    public async Task<AdminResult<SetupStatus>> StatusAsync(CurrentUser user, int terminalId, CancellationToken ct = default)
    {
        if (!AccessRules.CanViewCapacity(user, terminalId) || !(user.IsSysAdmin || user.IsTerminalAdministrator || user.Role == UserRole.Planner))
            return AdminResult<SetupStatus>.Forbidden("Only admins and planners of this terminal can see its set-up.");
        var terminal = await db.Terminals.AsNoTracking().SingleOrDefaultAsync(t => t.TerminalId == terminalId, ct);
        if (terminal is null)
            return AdminResult<SetupStatus>.NotFound($"Terminal {terminalId} not found.");

        var now = clock.GetUtcNow();
        var moveTypes = await db.TransactionCategories.CountAsync(c => c.TerminalId == terminalId && c.IsActive, ct);
        var plan = await db.CapacityTemplates.AnyAsync(t => t.TerminalId == terminalId && t.IsActive && t.IsDefault, ct);
        var slots = await db.Slots.CountAsync(s => s.TerminalId == terminalId && s.StartAt > now && !s.IsClosed, ct);
        var lanesIn = await db.GateLanes.CountAsync(l => l.TerminalId == terminalId && l.IsActive && l.Direction == LaneDirection.In, ct);
        var lanesOut = await db.GateLanes.CountAsync(l => l.TerminalId == terminalId && l.IsActive && l.Direction == LaneDirection.Out, ct);
        var staff = await db.AppUsers.CountAsync(u => u.IsActive
            && (u.TerminalId == terminalId || (terminal.OperatorId != null && u.OperatorId == terminal.OperatorId)), ct);
        var companies = await db.Companies.CountAsync(c => c.Status == CompanyStatus.Active, ct);
        var areas = await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM geofence WHERE terminal_id = {terminalId}").SingleAsync(ct);

        var adapter = profiles.TosFor(terminal.Code);
        var tosConfigured = adapter != "N4" || n4.CurrentValue.For(terminal.Code) is not null;
        var tosDetail = adapter switch
        {
            "N4" when tosConfigured => "Navis N4 (N4__Terminals__" + terminal.Code + "__…). Use “Test the connection”.",
            "N4" => $"N4 is chosen but not connected: set N4__Terminals__{terminal.Code}__Url and its account.",
            _ => $"Demo data, not a TOS: set Terminals__{terminal.Code}__Tos=N4 (or another adapter) to check real containers.",
        };

        IReadOnlyList<SetupStep> steps =
        [
            new("moveTypes", moveTypes > 0, false, $"{moveTypes} move type(s)", moveTypes),
            new("weeklyPlan", plan, false, plan ? "A default weekly plan" : "No weekly plan: no slots can be made"),
            new("slots", slots > 0, false, $"{slots} open slot(s) ahead", slots),
            new("lanes", lanesIn > 0 && lanesOut > 0, false, $"{lanesIn} in, {lanesOut} out", lanesIn, lanesOut),
            new("tos", adapter != "Fake" && tosConfigured, false, tosDetail),
            new("staff", staff > 0, false, $"{staff} staff account(s)", staff),
            new("companies", companies > 0, false, $"{companies} active trucking compan(ies)", companies),
            new("map", areas > 0, true, areas > 0 ? $"{areas} area(s) drawn" : "No areas: drivers get no directions inside, and the pre-gate check-in is off", areas),
        ];
        var ready = steps.Where(s => s.Key is "moveTypes" or "weeklyPlan" or "slots" or "lanes").All(s => s.Done);
        return AdminResult<SetupStatus>.Ok(new SetupStatus(terminalId, terminal.Code, ready, steps, adapter, tosConfigured));
    }

    /// <summary>Asks the terminal's TOS about a bill of lading that can't exist: any answer means it is reachable.</summary>
    public async Task<AdminResult<TosTestResult>> TestTosAsync(CurrentUser user, int terminalId, CancellationToken ct = default)
    {
        if (!AccessRules.CanManageCategories(user, terminalId))
            return AdminResult<TosTestResult>.Forbidden("Only admins of this terminal can test its TOS connection.");
        var code = await TerminalProfiles.CodeAsync(db, terminalId, ct);
        if (code is null)
            return AdminResult<TosTestResult>.NotFound($"Terminal {terminalId} not found.");

        var adapter = profiles.TosFor(code);
        var watch = Stopwatch.StartNew();
        try
        {
            await tos.FindBlContainersAsync(terminalId, "YARDSYNCS-CONNECTION-TEST", ct);
            return AdminResult<TosTestResult>.Ok(new TosTestResult(adapter, true,
                adapter == "Fake" ? "The demo TOS answered (demo data, not a real TOS)." : $"{adapter} answered.", watch.ElapsedMilliseconds));
        }
        catch (TosUnavailableException ex)
        {
            return AdminResult<TosTestResult>.Ok(new TosTestResult(adapter, false, ex.Message, watch.ElapsedMilliseconds));
        }
    }
}
