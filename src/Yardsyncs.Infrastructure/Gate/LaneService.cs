using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yardsyncs.Application.Auth;
using Yardsyncs.Application.Capacity;
using Yardsyncs.Application.Gate;
using Yardsyncs.Domain;
using Yardsyncs.Domain.Gate;
using Yardsyncs.Infrastructure.Persistence;

namespace Yardsyncs.Infrastructure.Gate;

/// <summary>
/// A terminal's gate lanes: code, gate, direction, and whether trucks can use them now (gate staff open and close them
/// with a note); terminal admins add lanes and retire them. Every change is in the audit log. With the Autogate module
/// a lane also has devices and can work automatically (<see cref="ILaneAddOn"/>). Endpoints check the access rules.
/// </summary>
public sealed class LaneService(
    YardsyncsDbContext db, TimeProvider clock, ILogger<LaneService> logger, ILaneAddOn? addOn = null, Modules? modules = null)
{
    public async Task<IReadOnlyList<LaneView>> ListAsync(int terminalId, CancellationToken ct = default)
    {
        var lanes = await db.GateLanes.AsNoTracking().Where(l => l.TerminalId == terminalId)
            .OrderBy(l => l.GateCode).ThenBy(l => l.LaneCode).ToListAsync(ct);
        var settings = await SettingsAsync(terminalId, ct);
        var extras = await ExtrasAsync(terminalId, lanes, ct);
        return lanes.Select(l => View(l, extras.GetValueOrDefault(l.LaneId) ?? LaneExtras.None, settings)).ToList();
    }

    public async Task<AdminResult<LaneView>> CreateAsync(CurrentUser admin, int terminalId, LaneInput input, CancellationToken ct = default)
    {
        var gate = input.GateCode?.Trim().ToUpperInvariant() ?? "";
        var code = input.LaneCode?.Trim().ToUpperInvariant() ?? "";
        if (gate.Length is 0 or > 20 || code.Length is 0 or > 20)
            return AdminResult<LaneView>.Invalid("Gate and lane codes are required (max 20 characters).");
        if (!Enum.IsDefined(input.Direction))
            return AdminResult<LaneView>.Invalid("Direction must be In or Out.");
        if (await db.GateLanes.AnyAsync(l => l.TerminalId == terminalId && l.LaneCode == code, ct))
            return AdminResult<LaneView>.Conflict($"Lane {code} already exists.");

        var lane = new GateLane { TerminalId = terminalId, GateCode = gate, LaneCode = code, Direction = input.Direction };
        db.GateLanes.Add(lane);
        await db.SaveChangesAsync(ct);
        var view = View(lane, LaneExtras.None, await SettingsAsync(terminalId, ct));
        AuditWriter.Add(db, terminalId, "gate_lane", lane.LaneId, AuditAction.Insert, null, view, null, admin.UserId, clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return AdminResult<LaneView>.Created(view);
    }

    /// <summary>Change a lane's gate, way of working, or retire / restore it. Automatic needs a barrier and a way to identify trucks.</summary>
    public async Task<AdminResult<LaneView>> UpdateAsync(CurrentUser admin, int terminalId, int laneId, LaneUpdateInput input, CancellationToken ct = default)
    {
        var lane = await db.GateLanes.SingleOrDefaultAsync(l => l.TerminalId == terminalId && l.LaneId == laneId, ct);
        if (lane is null)
            return AdminResult<LaneView>.NotFound($"Lane {laneId} not found.");
        var extras = await ExtrasAsync(lane, ct);
        var settings = await SettingsAsync(terminalId, ct);
        var before = View(lane, extras, settings);

        if (input.GateCode is not null)
        {
            var gate = input.GateCode.Trim().ToUpperInvariant();
            if (gate.Length is 0 or > 20)
                return AdminResult<LaneView>.Invalid("The gate code is required (max 20 characters).");
            lane.GateCode = gate;
        }
        if (input.Mode is { } mode)
        {
            if (!Enum.IsDefined(mode))
                return AdminResult<LaneView>.Invalid("Way of working must be Manual or Automatic.");
            if (mode == LaneMode.Automatic && mode != lane.Mode)
            {
                if (addOn is null || modules is null || !await modules.AutogateAtAsync(db, terminalId, ct))
                    return AdminResult<LaneView>.Invalid("Lanes that work automatically come with the Yardsyncs Autogate module, which this terminal doesn't have.");
                if (await addOn.AutomaticProblemAsync(lane, ct) is { } problem)
                    return AdminResult<LaneView>.Invalid(problem);
            }
            lane.Mode = mode;
        }
        if (input.IsActive is { } active)
            lane.IsActive = active;

        var after = View(lane, extras, settings);
        AuditWriter.Add(db, terminalId, "gate_lane", lane.LaneId, AuditAction.Update, before, after, null, admin.UserId, clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return AdminResult<LaneView>.Ok(after);
    }

    /// <summary>Open, close or mark a lane out of order. Closing needs a note, shown to whoever tries the lane.</summary>
    public async Task<AdminResult<LaneView>> SetStateAsync(CurrentUser user, int terminalId, int laneId, LaneStateInput input, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(input.State))
            return AdminResult<LaneView>.Invalid("State must be Open, Closed or OutOfOrder.");
        var note = string.IsNullOrWhiteSpace(input.Note) ? null : input.Note.Trim();
        if (input.State != LaneState.Open && note is null)
            return AdminResult<LaneView>.Invalid("Say why the lane is closed or out of order.");
        if (note?.Length > LaneRules.MaxNoteLength)
            return AdminResult<LaneView>.Invalid($"The note is too long (max {LaneRules.MaxNoteLength} characters).");

        var lane = await db.GateLanes.SingleOrDefaultAsync(l => l.TerminalId == terminalId && l.LaneId == laneId, ct);
        if (lane is null)
            return AdminResult<LaneView>.NotFound($"Lane {laneId} not found.");
        if (!lane.IsActive)
            return AdminResult<LaneView>.Conflict($"Lane {lane.LaneCode} is retired.");

        var extras = await ExtrasAsync(lane, ct);
        var settings = await SettingsAsync(terminalId, ct);
        var before = View(lane, extras, settings);
        var now = clock.GetUtcNow();
        lane.State = input.State;
        lane.StateNote = input.State == LaneState.Open ? null : note;
        lane.StateChangedAt = now;
        lane.StateChangedBy = user.UserId;
        var after = View(lane, extras, settings);
        AuditWriter.Add(db, terminalId, "gate_lane", lane.LaneId, AuditAction.Update, before, after, note, user.UserId, now);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Lane {LaneCode} at terminal {TerminalId} set to {State} by user {UserId}", lane.LaneCode, terminalId, input.State, user.UserId);
        return AdminResult<LaneView>.Ok(after);
    }

    private async Task<TerminalSettings> SettingsAsync(int terminalId, CancellationToken ct) =>
        TerminalSettings.From(await db.TerminalSettings.AsNoTracking().Where(s => s.TerminalId == terminalId).ToListAsync(ct));

    /// <summary>The lane's equipment, latest read and current truck, when the terminal has an add-on for that (Autogate).</summary>
    private async Task<LaneExtras> ExtrasAsync(GateLane lane, CancellationToken ct) =>
        (await ExtrasAsync(lane.TerminalId, [lane], ct)).GetValueOrDefault(lane.LaneId) ?? LaneExtras.None;

    private async Task<IReadOnlyDictionary<int, LaneExtras>> ExtrasAsync(int terminalId, IReadOnlyList<GateLane> lanes, CancellationToken ct) =>
        addOn is not null && modules is not null && await modules.AutogateAtAsync(db, terminalId, ct)
            ? await addOn.ExtrasAsync(terminalId, lanes, ct)
            : new Dictionary<int, LaneExtras>();

    private LaneView View(GateLane lane, LaneExtras extras, TerminalSettings settings) =>
        LaneView.From(lane, extras.Devices, settings, clock.GetUtcNow(), extras.LastRead, extras.Transaction);
}
