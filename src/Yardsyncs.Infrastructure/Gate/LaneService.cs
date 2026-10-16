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
/// The Autogate lane model: a terminal's gate lanes with their state and way of working, and the devices at each lane.
/// Gate staff open and close lanes (with a note); terminal admins add lanes and devices and switch a lane to automatic
/// once it has what it needs. Every change is in the audit log. Endpoints check the access rules.
/// </summary>
public sealed class LaneService(YardsyncsDbContext db, TimeProvider clock, ILogger<LaneService> logger)
{
    public async Task<IReadOnlyList<LaneView>> ListAsync(int terminalId, CancellationToken ct = default)
    {
        var lanes = await db.GateLanes.AsNoTracking().Where(l => l.TerminalId == terminalId)
            .OrderBy(l => l.GateCode).ThenBy(l => l.LaneCode).ToListAsync(ct);
        var devices = (await db.LaneDevices.AsNoTracking().Where(d => d.TerminalId == terminalId).ToListAsync(ct))
            .ToLookup(d => d.LaneId);
        var settings = await SettingsAsync(terminalId, ct);
        return lanes.Select(l => LaneView.From(l, devices[l.LaneId].ToList(), settings)).ToList();
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
        var view = LaneView.From(lane, [], await SettingsAsync(terminalId, ct));
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
        var devices = await DevicesAsync(laneId, ct);
        var settings = await SettingsAsync(terminalId, ct);
        var before = LaneView.From(lane, devices, settings);

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
            if (mode == LaneMode.Automatic && LaneRules.AutomaticProblems(devices, settings) is { Count: > 0 } problems)
                return AdminResult<LaneView>.Invalid($"Lane {lane.LaneCode} can't work automatically yet: it needs {LaneRules.Needs(problems)}.");
            lane.Mode = mode;
        }
        if (input.IsActive is { } active)
            lane.IsActive = active;

        var after = LaneView.From(lane, devices, settings);
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

        var devices = await DevicesAsync(laneId, ct);
        var settings = await SettingsAsync(terminalId, ct);
        var before = LaneView.From(lane, devices, settings);
        var now = clock.GetUtcNow();
        lane.State = input.State;
        lane.StateNote = input.State == LaneState.Open ? null : note;
        lane.StateChangedAt = now;
        lane.StateChangedBy = user.UserId;
        var after = LaneView.From(lane, devices, settings);
        AuditWriter.Add(db, terminalId, "gate_lane", lane.LaneId, AuditAction.Update, before, after, note, user.UserId, now);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Lane {LaneCode} at terminal {TerminalId} set to {State} by user {UserId}", lane.LaneCode, terminalId, input.State, user.UserId);
        return AdminResult<LaneView>.Ok(after);
    }

    // ================================================================ devices

    public Task<AdminResult<LaneView>> AddDeviceAsync(CurrentUser admin, int terminalId, int laneId, LaneDeviceInput input, CancellationToken ct = default) =>
        SaveDeviceAsync(admin, terminalId, laneId, null, input, ct);

    public Task<AdminResult<LaneView>> UpdateDeviceAsync(CurrentUser admin, int terminalId, int laneId, int deviceId, LaneDeviceInput input, CancellationToken ct = default) =>
        SaveDeviceAsync(admin, terminalId, laneId, deviceId, input, ct);

    /// <summary>Remove a device. An automatic lane must keep what it needs; switch it to manual first.</summary>
    public async Task<AdminResult<LaneView>> RemoveDeviceAsync(CurrentUser admin, int terminalId, int laneId, int deviceId, CancellationToken ct = default)
    {
        var lane = await db.GateLanes.SingleOrDefaultAsync(l => l.TerminalId == terminalId && l.LaneId == laneId, ct);
        var device = await db.LaneDevices.SingleOrDefaultAsync(d => d.TerminalId == terminalId && d.LaneId == laneId && d.DeviceId == deviceId, ct);
        if (lane is null || device is null)
            return AdminResult<LaneView>.NotFound($"Device {deviceId} not found at lane {laneId}.");

        var settings = await SettingsAsync(terminalId, ct);
        var remaining = (await DevicesAsync(laneId, ct)).Where(d => d.DeviceId != deviceId).ToList();
        if (StillAutomatic(lane, remaining, settings) is { } problem)
            return AdminResult<LaneView>.Conflict(problem);

        db.LaneDevices.Remove(device);
        AuditWriter.Add(db, terminalId, "gate_lane_device", device.DeviceId, AuditAction.Delete, LaneDeviceView.From(device), null, null, admin.UserId, clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return AdminResult<LaneView>.Ok(LaneView.From(lane, remaining, settings));
    }

    private async Task<AdminResult<LaneView>> SaveDeviceAsync(
        CurrentUser admin, int terminalId, int laneId, int? deviceId, LaneDeviceInput input, CancellationToken ct)
    {
        var errors = LaneRules.Validate(input.DeviceCode, input.Kind, input.Reads).ToList();
        if (Longer(input.Vendor, 60) || Longer(input.Model, 60)) errors.Add("Vendor and model are at most 60 characters.");
        if (Longer(input.Address, 200)) errors.Add("The address is at most 200 characters.");
        if (Longer(input.Notes, 200)) errors.Add("Notes are at most 200 characters.");
        if (errors.Count > 0)
            return AdminResult<LaneView>.Invalid(errors);

        var lane = await db.GateLanes.SingleOrDefaultAsync(l => l.TerminalId == terminalId && l.LaneId == laneId, ct);
        if (lane is null)
            return AdminResult<LaneView>.NotFound($"Lane {laneId} not found.");
        var code = input.DeviceCode.Trim().ToUpperInvariant();
        if (await db.LaneDevices.AnyAsync(d => d.TerminalId == terminalId && d.DeviceCode == code && d.DeviceId != (deviceId ?? 0), ct))
            return AdminResult<LaneView>.Conflict($"Device {code} already exists at this terminal.");

        GateLaneDevice device;
        object? before = null;
        if (deviceId is { } id)
        {
            var existing = await db.LaneDevices.SingleOrDefaultAsync(d => d.TerminalId == terminalId && d.LaneId == laneId && d.DeviceId == id, ct);
            if (existing is null)
                return AdminResult<LaneView>.NotFound($"Device {id} not found at lane {lane.LaneCode}.");
            device = existing;
            before = LaneDeviceView.From(device);
        }
        else
            device = new GateLaneDevice { TerminalId = terminalId, LaneId = laneId, CreatedAt = clock.GetUtcNow() };

        device.DeviceCode = code;
        device.Kind = input.Kind;
        device.Reads = LaneRules.FormatReads(input.Reads);
        device.Vendor = Clean(input.Vendor);
        device.Model = Clean(input.Model);
        device.Address = Clean(input.Address);
        device.IsActive = input.IsActive;
        device.Notes = Clean(input.Notes);

        var settings = await SettingsAsync(terminalId, ct);
        var devices = (await DevicesAsync(laneId, ct)).Where(d => d.DeviceId != deviceId).ToList();
        devices.Add(device);
        if (StillAutomatic(lane, devices, settings) is { } problem)
            return AdminResult<LaneView>.Conflict(problem);

        if (deviceId is null)
            db.LaneDevices.Add(device);
        await db.SaveChangesAsync(ct);
        AuditWriter.Add(db, terminalId, "gate_lane_device", device.DeviceId, deviceId is null ? AuditAction.Insert : AuditAction.Update,
            before, LaneDeviceView.From(device), null, admin.UserId, clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return deviceId is null
            ? AdminResult<LaneView>.Created(LaneView.From(lane, devices, settings))
            : AdminResult<LaneView>.Ok(LaneView.From(lane, devices, settings));
    }

    /// <summary>For an automatic lane: why the device change would leave it unable to work, or null.</summary>
    private static string? StillAutomatic(GateLane lane, IReadOnlyList<GateLaneDevice> devices, TerminalSettings settings) =>
        lane.Mode == LaneMode.Automatic && LaneRules.AutomaticProblems(devices, settings) is { Count: > 0 } problems
            ? $"Lane {lane.LaneCode} works automatically and would then lack {LaneRules.Needs(problems)}. Switch it to manual first."
            : null;

    private Task<List<GateLaneDevice>> DevicesAsync(int laneId, CancellationToken ct) =>
        db.LaneDevices.AsNoTracking().Where(d => d.LaneId == laneId).ToListAsync(ct);

    private async Task<TerminalSettings> SettingsAsync(int terminalId, CancellationToken ct) =>
        TerminalSettings.From(await db.TerminalSettings.AsNoTracking().Where(s => s.TerminalId == terminalId).ToListAsync(ct));

    private static bool Longer(string? value, int max) => value?.Trim().Length > max;

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
