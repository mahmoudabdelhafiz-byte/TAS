using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yardsyncs.Application.Auth;
using Yardsyncs.Application.Capacity;
using Yardsyncs.Application.Gate;
using Yardsyncs.Domain.Gate;
using Yardsyncs.Infrastructure.Persistence;

namespace Yardsyncs.Autogate;

/// <summary>
/// Autogate device adapters, vendor-neutral. A lane device — or the vendor adapter speaking for it — signs in with its
/// own key (X-Device-Key), sends what it read, reports its health, and fetches the commands meant for it (barrier,
/// light, display, printer), confirming each. Pull, not push: devices on the terminal's network call out, so nothing
/// needs to reach into it. Gate staff send commands to a lane and see its latest reads and commands.
/// </summary>
public sealed class DeviceService(YardsyncsDbContext db, TimeProvider clock, ILogger<DeviceService> logger, Modules? modules = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ================================================================ device side

    /// <summary>The active device at an active lane that owns this key, or null. Every call counts as contact.</summary>
    public async Task<GateLaneDevice?> AuthenticateAsync(string? key, CancellationToken ct = default)
    {
        if (!DeviceKeys.LooksLikeKey(key))
            return null;
        var hash = DeviceKeys.Hash(key!);
        var device = await db.LaneDevices.SingleOrDefaultAsync(d => d.KeyHash == hash && d.IsActive, ct);
        if (device is null || !await db.GateLanes.AnyAsync(l => l.LaneId == device.LaneId && l.IsActive, ct))
            return null;
        if (modules is not null && !await modules.AutogateAtAsync(db, device.TerminalId, ct))
            return null; // the terminal no longer has Autogate
        device.LastSeenAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return device;
    }

    public async Task<DeviceIdentity> IdentityAsync(GateLaneDevice device, CancellationToken ct = default)
    {
        var lane = await db.GateLanes.AsNoTracking().SingleAsync(l => l.LaneId == device.LaneId, ct);
        var terminal = await db.Terminals.AsNoTracking().Where(t => t.TerminalId == device.TerminalId).Select(t => t.Code).SingleAsync(ct);
        return new DeviceIdentity(device.TerminalId, terminal, lane.LaneCode, lane.Direction, device.DeviceCode, device.Kind);
    }

    /// <summary>Store a read, normalized. Refused when it holds what this kind of device can't produce.</summary>
    public async Task<AdminResult<DeviceReadView>> ReadAsync(GateLaneDevice device, DeviceReadInput input, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var (errors, read) = DeviceRules.Check(device.Kind, input, now);
        if (read is null)
            return AdminResult<DeviceReadView>.Invalid(errors);

        var row = new LaneDeviceRead
        {
            TerminalId = device.TerminalId,
            LaneId = device.LaneId,
            DeviceId = device.DeviceId,
            ReadAt = read.ReadAt,
            ReceivedAt = now,
            Plate = read.Plate,
            PlateConfidence = (short?)read.PlateConfidence,
            ContainerNo = read.Containers.FirstOrDefault()?.Number,
            Payload = JsonSerializer.Serialize(new Payload(
                read.Containers, read.Seals, read.Damage, read.Photos, read.Tag, read.AppointmentNo, read.QrToken, read.WeightKg, read.Warnings), Json),
        };
        db.LaneDeviceReads.Add(row);
        await db.SaveChangesAsync(ct);
        return AdminResult<DeviceReadView>.Created(View(row, device.DeviceCode));
    }

    public async Task<AdminResult<DeviceIdentity>> StatusAsync(GateLaneDevice device, DeviceStatusInput input, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(input.Status))
            return AdminResult<DeviceIdentity>.Invalid("Status is Ok or Fault.");
        var detail = string.IsNullOrWhiteSpace(input.Detail) ? null : input.Detail.Trim();
        if (detail?.Length > 200)
            return AdminResult<DeviceIdentity>.Invalid("The detail is at most 200 characters.");
        if (input.Status == DeviceHealth.Fault && device.ReportedStatus != DeviceHealth.Fault)
            logger.LogWarning("Lane device {DeviceCode} at terminal {TerminalId} reports a fault: {Detail}", device.DeviceCode, device.TerminalId, detail);
        device.ReportedStatus = input.Status;
        device.StatusDetail = detail;
        await db.SaveChangesAsync(ct);
        return AdminResult<DeviceIdentity>.Ok(await IdentityAsync(device, ct));
    }

    /// <summary>The device's commands still to carry out, oldest first; each is marked delivered. Expired ones are never handed out.</summary>
    public async Task<IReadOnlyList<DeviceCommandView>> PendingAsync(GateLaneDevice device, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var pending = await db.LaneDeviceCommands
            .Where(c => c.DeviceId == device.DeviceId && c.DoneAt == null && c.ExpiresAt > now)
            .OrderBy(c => c.CreatedAt).ThenBy(c => c.CommandId)
            .Take(20).ToListAsync(ct);
        foreach (var c in pending.Where(c => c.DeliveredAt is null))
            c.DeliveredAt = now;
        await db.SaveChangesAsync(ct);
        return pending.Select(c => new DeviceCommandView(c.CommandId, c.Command, c.Text, c.CreatedAt, c.ExpiresAt)).ToList();
    }

    public async Task<AdminResult<LaneCommandView>> AckAsync(GateLaneDevice device, long commandId, DeviceAckInput input, CancellationToken ct = default)
    {
        var command = await db.LaneDeviceCommands.SingleOrDefaultAsync(c => c.CommandId == commandId && c.DeviceId == device.DeviceId, ct);
        if (command is null)
            return AdminResult<LaneCommandView>.NotFound($"Command {commandId} is not for this device.");
        if (command.DoneAt is not null)
            return AdminResult<LaneCommandView>.Conflict($"Command {commandId} was already confirmed ({command.Result}).");
        var detail = string.IsNullOrWhiteSpace(input.Detail) ? null : input.Detail.Trim();
        if (detail?.Length > 200)
            return AdminResult<LaneCommandView>.Invalid("The detail is at most 200 characters.");

        var now = clock.GetUtcNow();
        command.DeliveredAt ??= now;
        command.DoneAt = now;
        command.Result = input.Done ? CommandResult.Done : CommandResult.Failed;
        command.ResultDetail = detail;
        await db.SaveChangesAsync(ct);
        if (!input.Done)
            logger.LogWarning("Lane device {DeviceCode} could not carry out {Command}: {Detail}", device.DeviceCode, command.Command, detail);
        return AdminResult<LaneCommandView>.Ok(View(command, device.DeviceCode, now));
    }

    // ================================================================ gate side

    /// <summary>
    /// Queue a command for a lane: for the named device, or every active device at the lane that takes it (Open → its
    /// barriers…). The lane controller sends its commands the same way; staff commands carry who sent them.
    /// </summary>
    public async Task<AdminResult<IReadOnlyList<LaneCommandView>>> SendAsync(CurrentUser? user, int terminalId, int laneId, LaneCommandInput input, CancellationToken ct = default)
    {
        if (DeviceRules.CommandProblem(input.Command, input.Text) is { } problem)
            return AdminResult<IReadOnlyList<LaneCommandView>>.Invalid(problem);
        var lane = await db.GateLanes.AsNoTracking().SingleOrDefaultAsync(l => l.TerminalId == terminalId && l.LaneId == laneId, ct);
        if (lane is null)
            return AdminResult<IReadOnlyList<LaneCommandView>>.NotFound($"Lane {laneId} not found.");

        var kinds = DeviceRules.TakenBy(input.Command);
        var devices = await db.LaneDevices.AsNoTracking().Where(d => d.LaneId == laneId && d.IsActive).ToListAsync(ct);
        var code = input.DeviceCode?.Trim().ToUpperInvariant();
        var targets = devices.Where(d => kinds.Contains(d.Kind) && (code is null || d.DeviceCode == code)).ToList();
        if (targets.Count == 0)
            return AdminResult<IReadOnlyList<LaneCommandView>>.Conflict(code is null
                ? $"Lane {lane.LaneCode} has no device in use that can {input.Command.ToString().ToLowerInvariant()}."
                : $"{code} is not a device in use at lane {lane.LaneCode} that can {input.Command.ToString().ToLowerInvariant()}.");

        var now = clock.GetUtcNow();
        var text = string.IsNullOrWhiteSpace(input.Text) ? null : input.Text.Trim();
        var rows = targets.Select(d => new LaneDeviceCommand
        {
            TerminalId = terminalId,
            LaneId = laneId,
            DeviceId = d.DeviceId,
            Command = input.Command,
            Text = text,
            CreatedAt = now,
            CreatedBy = user?.UserId,
            ExpiresAt = now + DeviceRules.Lifetime(input.Command),
        }).ToList();
        db.LaneDeviceCommands.AddRange(rows);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("{Command} sent to lane {LaneCode} ({Devices}) by {Who}",
            input.Command, lane.LaneCode, string.Join(",", targets.Select(t => t.DeviceCode)), user is null ? "the lane" : $"user {user.UserId}");
        var codes = targets.ToDictionary(t => t.DeviceId, t => t.DeviceCode);
        return AdminResult<IReadOnlyList<LaneCommandView>>.Created(rows.Select(r => View(r, codes[r.DeviceId], now)).ToList());
    }

    /// <summary>
    /// The lane controller's commands for a truck: each goes to the lane's devices in use that carry it out; commands for
    /// devices the lane doesn't have are skipped.
    /// </summary>
    public async Task<int> QueueAsync(int terminalId, int laneId, long transactionId, IReadOnlyList<PlannedCommand> commands, CancellationToken ct = default)
    {
        var devices = await db.LaneDevices.AsNoTracking().Where(d => d.LaneId == laneId && d.IsActive).ToListAsync(ct);
        var now = clock.GetUtcNow();
        var rows = new List<LaneDeviceCommand>();
        foreach (var (command, i) in commands.Select((c, i) => (c, i)))
        {
            var kinds = DeviceRules.TakenBy(command.Command);
            rows.AddRange(devices.Where(d => kinds.Contains(d.Kind)).Select(d => new LaneDeviceCommand
            {
                TerminalId = terminalId,
                LaneId = laneId,
                DeviceId = d.DeviceId,
                Command = command.Command,
                Text = command.Text,
                CreatedAt = now.AddTicks(i * 10), // keeps the order (to the microsecond): green before open
                ExpiresAt = now + DeviceRules.Lifetime(command.Command),
                TransactionId = transactionId,
            }));
        }
        db.LaneDeviceCommands.AddRange(rows);
        await db.SaveChangesAsync(ct);
        return rows.Count;
    }

    /// <summary>The lane's latest reads and commands, newest first.</summary>
    public async Task<LaneActivity?> ActivityAsync(int terminalId, int laneId, int limit = 20, CancellationToken ct = default)
    {
        if (!await db.GateLanes.AnyAsync(l => l.TerminalId == terminalId && l.LaneId == laneId, ct))
            return null;
        limit = Math.Clamp(limit, 1, 100);
        var codes = await db.LaneDevices.AsNoTracking().Where(d => d.LaneId == laneId).ToDictionaryAsync(d => d.DeviceId, d => d.DeviceCode, ct);
        var reads = await db.LaneDeviceReads.AsNoTracking().Where(r => r.LaneId == laneId)
            .OrderByDescending(r => r.ReceivedAt).ThenByDescending(r => r.ReadId).Take(limit).ToListAsync(ct);
        var commands = await db.LaneDeviceCommands.AsNoTracking().Where(c => c.LaneId == laneId)
            .OrderByDescending(c => c.CreatedAt).ThenByDescending(c => c.CommandId).Take(limit).ToListAsync(ct);
        var now = clock.GetUtcNow();
        return new LaneActivity(
            reads.Select(r => View(r, codes.GetValueOrDefault(r.DeviceId, "?"))).ToList(),
            commands.Select(c => View(c, codes.GetValueOrDefault(c.DeviceId, "?"), now)).ToList());
    }

    // ================================================================ views

    public static DeviceReadView View(LaneDeviceRead r, string deviceCode)
    {
        Payload? p = null;
        try
        {
            p = JsonSerializer.Deserialize<Payload>(r.Payload, Json);
        }
        catch (JsonException)
        {
            // An unreadable payload shows as the plate and container columns only.
        }
        return new DeviceReadView(
            r.ReadId, deviceCode, r.ReadAt, r.ReceivedAt, r.Plate, r.PlateConfidence,
            p?.Containers ?? (r.ContainerNo is null ? [] : [new ContainerReadView(r.ContainerNo, true, null, null, null)]),
            p?.Seals ?? [], p?.Damage ?? [], p?.Photos ?? [], p?.Tag, p?.AppointmentNo, p?.QrToken is not null, p?.WeightKg, p?.Warnings ?? []);
    }

    /// <summary>A stored read as the lane controller works with it, gate pass included.</summary>
    public static NormalizedRead Normalized(LaneDeviceRead r)
    {
        Payload? p = null;
        try
        {
            p = JsonSerializer.Deserialize<Payload>(r.Payload, Json);
        }
        catch (JsonException)
        {
            // Plate and first container only.
        }
        return new NormalizedRead(
            r.ReadAt, r.Plate, r.PlateConfidence,
            p?.Containers ?? (r.ContainerNo is null ? [] : [new ContainerReadView(r.ContainerNo, true, null, null, null)]),
            p?.Seals ?? [], p?.Damage ?? [], p?.Photos ?? [], p?.Tag, p?.AppointmentNo, p?.QrToken, p?.WeightKg, p?.Warnings ?? []);
    }

    public static LaneCommandView View(LaneDeviceCommand c, string deviceCode, DateTimeOffset now) =>
        new(c.CommandId, deviceCode, c.Command, c.Text, c.CreatedAt, c.ExpiresAt, c.DeliveredAt, c.DoneAt, c.Result, c.ResultDetail,
            DeviceRules.Progress(c, now));

    /// <summary>The stored read beyond the plate and first container. The gate pass is kept for the lane controller to match.</summary>
    private sealed record Payload(
        IReadOnlyList<ContainerReadView>? Containers,
        IReadOnlyList<string>? Seals,
        IReadOnlyList<DamageView>? Damage,
        IReadOnlyList<string>? Photos,
        string? Tag,
        string? AppointmentNo,
        string? QrToken,
        decimal? WeightKg,
        IReadOnlyList<string>? Warnings);
}
