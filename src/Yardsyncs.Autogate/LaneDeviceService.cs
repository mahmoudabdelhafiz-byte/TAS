using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yardsyncs.Application.Auth;
using Yardsyncs.Application.Capacity;
using Yardsyncs.Application.Gate;
using Yardsyncs.Domain;
using Yardsyncs.Domain.Gate;

namespace Yardsyncs.Autogate;

/// <summary>
/// The devices at the gate's lanes (cameras, OCR, RFID readers, kiosks, barriers, lights, displays, printers,
/// intercoms, weighbridges) and their keys; and, for the core lane list, each lane's devices, latest read and current
/// truck. Terminal admins add devices; an automatic lane keeps what it needs to work. Every change is in the audit log.
/// </summary>
public sealed class LaneDeviceService(YardsyncsDbContext db, TimeProvider clock, ILogger<LaneDeviceService> logger) : ILaneAddOn
{
    /// <summary>How long a read counts as the lane's latest on the gate screen.</summary>
    public static readonly TimeSpan LastReadWindow = TimeSpan.FromMinutes(10);

    // ================================================================ ILaneAddOn

    public async Task<IReadOnlyDictionary<int, LaneExtras>> ExtrasAsync(int terminalId, IReadOnlyList<GateLane> lanes, CancellationToken ct)
    {
        var laneIds = lanes.Select(l => l.LaneId).ToList();
        var devices = (await db.LaneDevices.AsNoTracking().Where(d => laneIds.Contains(d.LaneId)).ToListAsync(ct)).ToLookup(d => d.LaneId);
        var since = clock.GetUtcNow() - LastReadWindow;
        var recent = await db.LaneDeviceReads.AsNoTracking()
            .Where(r => r.TerminalId == terminalId && laneIds.Contains(r.LaneId) && r.ReceivedAt >= since)
            .OrderByDescending(r => r.ReceivedAt).Take(500).ToListAsync(ct);
        var codes = devices.SelectMany(g => g).ToDictionary(d => d.DeviceId, d => d.DeviceCode);
        var lastRead = recent.GroupBy(r => r.LaneId)
            .ToDictionary(g => g.Key, g => DeviceService.View(g.First(), codes.GetValueOrDefault(g.First().DeviceId, "?")));
        var transactions = (await db.LaneTransactions.AsNoTracking()
                .Where(t => t.TerminalId == terminalId && laneIds.Contains(t.LaneId) && t.LastReadAt >= since)
                .OrderByDescending(t => t.LastReadAt).ThenByDescending(t => t.TransactionId).Take(200).ToListAsync(ct))
            .GroupBy(t => t.LaneId).ToDictionary(g => g.Key, g => g.First());

        return lanes.ToDictionary(l => l.LaneId, l => new LaneExtras(
            devices[l.LaneId].ToList(),
            lastRead.GetValueOrDefault(l.LaneId),
            transactions.TryGetValue(l.LaneId, out var t) ? LaneTransactionView.From(t, l.LaneCode) : null));
    }

    public async Task<string?> AutomaticProblemAsync(GateLane lane, CancellationToken ct) =>
        LaneRules.AutomaticProblems(await DevicesAsync(lane.LaneId, ct), await SettingsAsync(lane.TerminalId, ct)) is { Count: > 0 } problems
            ? $"Lane {lane.LaneCode} can't work automatically yet: it needs {LaneRules.Needs(problems)}."
            : null;

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
        AuditWriter.Add(db, terminalId, "gate_lane_device", device.DeviceId, AuditAction.Delete, LaneDeviceView.From(device, clock.GetUtcNow()), null, null, admin.UserId, clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return AdminResult<LaneView>.Ok(await ViewAsync(lane, settings, ct));
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
            before = LaneDeviceView.From(device, clock.GetUtcNow());
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
            before, LaneDeviceView.From(device, clock.GetUtcNow()), null, admin.UserId, clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        var view = await ViewAsync(lane, settings, ct);
        return deviceId is null ? AdminResult<LaneView>.Created(view) : AdminResult<LaneView>.Ok(view);
    }

    /// <summary>
    /// Issue a new key for a device (any earlier key stops working). The key is returned once; only its fingerprint is
    /// kept. The device or its vendor adapter sends it as X-Device-Key.
    /// </summary>
    public async Task<AdminResult<DeviceKeyView>> IssueKeyAsync(CurrentUser admin, int terminalId, int laneId, int deviceId, CancellationToken ct = default)
    {
        var device = await db.LaneDevices.SingleOrDefaultAsync(d => d.TerminalId == terminalId && d.LaneId == laneId && d.DeviceId == deviceId, ct);
        if (device is null)
            return AdminResult<DeviceKeyView>.NotFound($"Device {deviceId} not found at lane {laneId}.");
        var key = DeviceKeys.New();
        var now = clock.GetUtcNow();
        var replaced = device.KeyHash is not null;
        device.KeyHash = DeviceKeys.Hash(key);
        device.KeySetAt = now;
        device.LastSeenAt = null;
        device.ReportedStatus = null;
        device.StatusDetail = null;
        AuditWriter.Add(db, terminalId, "gate_lane_device", device.DeviceId, AuditAction.Update, null,
            new { device.DeviceCode, KeyIssued = now }, replaced ? "Device key replaced" : "Device key issued", admin.UserId, now);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Device key {Action} for {DeviceCode} at terminal {TerminalId} by user {UserId}",
            replaced ? "replaced" : "issued", device.DeviceCode, terminalId, admin.UserId);
        return AdminResult<DeviceKeyView>.Created(new DeviceKeyView(device.DeviceCode, key));
    }

    /// <summary>For an automatic lane: why the device change would leave it unable to work, or null.</summary>
    private static string? StillAutomatic(GateLane lane, IReadOnlyList<GateLaneDevice> devices, TerminalSettings settings) =>
        lane.Mode == LaneMode.Automatic && LaneRules.AutomaticProblems(devices, settings) is { Count: > 0 } problems
            ? $"Lane {lane.LaneCode} works automatically and would then lack {LaneRules.Needs(problems)}. Switch it to manual first."
            : null;

    private async Task<LaneView> ViewAsync(GateLane lane, TerminalSettings settings, CancellationToken ct)
    {
        var extras = (await ExtrasAsync(lane.TerminalId, [lane], ct))[lane.LaneId];
        return LaneView.From(lane, extras.Devices, settings, clock.GetUtcNow(), extras.LastRead, extras.Transaction);
    }

    private Task<List<GateLaneDevice>> DevicesAsync(int laneId, CancellationToken ct) =>
        db.LaneDevices.AsNoTracking().Where(d => d.LaneId == laneId).ToListAsync(ct);

    private async Task<TerminalSettings> SettingsAsync(int terminalId, CancellationToken ct) =>
        TerminalSettings.From(await db.TerminalSettings.AsNoTracking().Where(s => s.TerminalId == terminalId).ToListAsync(ct));

    private static bool Longer(string? value, int max) => value?.Trim().Length > max;

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
