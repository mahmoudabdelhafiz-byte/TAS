using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Yardsyncs.Application.Gate;
using Yardsyncs.Domain;
using Yardsyncs.Domain.Gate;
using Yardsyncs.Infrastructure.Persistence;

namespace Yardsyncs.Infrastructure.Gate;

// The gate's extension points. The truck appointment system's gate (arrival, gate-in, gate-out, trouble desk, lanes)
// works on its own — for a clerk, or for the terminal's own gate system through the gate API. An add-on module such as
// Yardsyncs Autogate plugs in here; the core never references it.

/// <summary>A truck passing the gate.</summary>
/// <param name="UserId">The clerk or gate system account; null when an automatic lane did it.</param>
public sealed record GatePassage(int TerminalId, long AppointmentId, string? LaneCode, DateTimeOffset At, int? UserId);

/// <summary>
/// Told about gate passages. The gate-in and gate-out calls run before the gate saves, in the same unit of work, so what
/// a hook adds to the database is saved with the passage, or not at all.
/// </summary>
public interface IGateHook
{
    Task GateInAsync(GatePassage passage, CancellationToken ct);

    Task GateOutAsync(GatePassage passage, CancellationToken ct);

    /// <summary>After a trouble-desk case was resolved and saved.</summary>
    Task TroubleResolvedAsync(int terminalId, long troubleId, TroubleResolution resolution, CancellationToken ct);
}

/// <summary>A truck at an exit lane, as the exit check sees it.</summary>
public sealed record ExitContext(
    int TerminalId, GateLane Lane, long AppointmentId, int? TruckId, string? PlateRead,
    IReadOnlyList<string>? ContainerReads, decimal? WeightKg, IReadOnlyList<ExitItem> Items, DateTimeOffset At);

/// <summary>
/// Checks what is on a truck leaving against its visit, and records the exit. Without one, the gate lets trucks out
/// unchecked (NotChecked).
/// </summary>
public interface IExitCheck
{
    Task<ExitCheckResult> CheckAsync(ExitContext exit, CancellationToken ct);

    /// <summary>Records the exit before the gate saves; returns the cargo's net weight when the truck was weighed in and out.</summary>
    Task<decimal?> RecordAsync(ExitContext exit, ExitCheckResult result, int? letOutBy, string? note, CancellationToken ct);
}

/// <summary>What an add-on knows about a lane beyond its code, direction and state: devices, latest read, current truck.</summary>
public sealed record LaneExtras(IReadOnlyList<GateLaneDevice> Devices, DeviceReadView? LastRead, LaneTransactionView? Transaction)
{
    public static readonly LaneExtras None = new([], null, null);
}

/// <summary>Adds equipment to the gate's lanes (Autogate). Without one, lanes are worked by clerks or the terminal's own gate system.</summary>
public interface ILaneAddOn
{
    Task<IReadOnlyDictionary<int, LaneExtras>> ExtrasAsync(int terminalId, IReadOnlyList<GateLane> lanes, CancellationToken ct);

    /// <summary>Why this lane can't work automatically yet (null: it can).</summary>
    Task<string?> AutomaticProblemAsync(GateLane lane, CancellationToken ct);
}

/// <summary>The modules this installation runs (configuration Modules:*), and which a terminal has (terminal settings).</summary>
public sealed class Modules(IConfiguration config)
{
    /// <summary>Yardsyncs Autogate is installed (Modules:Autogate, default true). A truck appointment system without it: false.</summary>
    public bool AutogateInstalled => config.GetValue("Modules:Autogate", true);

    /// <summary>Autogate is installed and this terminal has it (setting module_autogate, on unless switched off).</summary>
    public async Task<bool> AutogateAtAsync(YardsyncsDbContext db, int terminalId, CancellationToken ct = default) =>
        AutogateInstalled && !await db.TerminalSettings.AsNoTracking()
            .AnyAsync(s => s.TerminalId == terminalId && s.SettingKey == TerminalSettings.ModuleAutogateKey && s.SettingValue == "0", ct);

    /// <summary>The terminals among these that have Autogate.</summary>
    public async Task<IReadOnlySet<int>> AutogateTerminalsAsync(YardsyncsDbContext db, IReadOnlyCollection<int> terminalIds, CancellationToken ct = default)
    {
        if (!AutogateInstalled)
            return new HashSet<int>();
        var off = await db.TerminalSettings.AsNoTracking()
            .Where(s => terminalIds.Contains(s.TerminalId) && s.SettingKey == TerminalSettings.ModuleAutogateKey && s.SettingValue == "0")
            .Select(s => s.TerminalId).ToListAsync(ct);
        return terminalIds.Except(off).ToHashSet();
    }
}
