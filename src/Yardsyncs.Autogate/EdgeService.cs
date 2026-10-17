using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yardsyncs.Application.Auth;
using Yardsyncs.Application.Edge;
using Yardsyncs.Domain;
using Yardsyncs.Domain.Booking;
using Yardsyncs.Domain.Gate;
using Yardsyncs.Infrastructure.DriverApp;

namespace Yardsyncs.Autogate;

/// <summary>
/// Edge mode: what the terminal's lane agent keeps to run the automatic lanes when the cloud can't be reached (the
/// snapshot), and what it decided meanwhile, recorded when it is back (the passages).
/// </summary>
public sealed class EdgeService(YardsyncsDbContext db, GateService gate, GatePassService passes, TimeProvider clock, ILogger<EdgeService> logger)
{
    /// <summary>Appointments in the snapshot: their slot started up to a day ago or starts within this time.</summary>
    public static readonly TimeSpan Horizon = TimeSpan.FromHours(36);

    public const int MaxPassagesPerCall = 500;

    public async Task<EdgeSnapshot?> SnapshotAsync(int terminalId, CancellationToken ct = default)
    {
        var terminal = await db.Terminals.AsNoTracking().SingleOrDefaultAsync(t => t.TerminalId == terminalId, ct);
        if (terminal is null)
            return null;
        var now = clock.GetUtcNow();
        var settings = TerminalSettings.From(await db.TerminalSettings.AsNoTracking().Where(s => s.TerminalId == terminalId).ToListAsync(ct));

        var lanes = await db.GateLanes.AsNoTracking().Where(l => l.TerminalId == terminalId).OrderBy(l => l.LaneCode).ToListAsync(ct);
        var laneCodes = lanes.ToDictionary(l => l.LaneId, l => l.LaneCode);
        var devices = await db.LaneDevices.AsNoTracking()
            .Where(d => d.TerminalId == terminalId && d.IsActive && d.KeyHash != null)
            .OrderBy(d => d.DeviceCode).ToListAsync(ct);

        var from = now.AddHours(-24);
        var to = now + Horizon;
        var appointments = await db.Appointments.AsNoTracking()
            .Where(a => a.TerminalId == terminalId && a.Slot.StartAt >= from && a.Slot.StartAt <= to
                        && (a.Status == AppointmentStatus.Confirmed || a.Status == AppointmentStatus.EnRoute
                            || a.Status == AppointmentStatus.Arrived || a.Status == AppointmentStatus.GateIn))
            .Select(a => new
            {
                a.AppointmentId, a.AppointmentNo, a.QrToken, a.Status, SlotStart = a.Slot.StartAt, SlotEnd = a.Slot.EndAt,
                Plate = a.Truck != null ? a.Truck.PlateNormalized : null,
                Containers = a.Items.Where(i => i.ContainerNo != null).Select(i => i.ContainerNo!).ToList(),
                HasDriver = a.DriverId != null,
                DriverMobile = a.Driver != null ? a.Driver.Mobile : null,
                TruckBlocked = a.Truck != null && a.Truck.IsBlocked,
                DriverBlocked = a.Driver != null && a.Driver.IsBlocked,
                a.TrafficLight, a.TrafficReason,
            })
            .OrderBy(a => a.SlotStart).ThenBy(a => a.AppointmentId)
            .ToListAsync(ct);
        // Trucks inside since earlier still have to get out.
        var inside = await db.Appointments.AsNoTracking()
            .Where(a => a.TerminalId == terminalId && a.Status == AppointmentStatus.GateIn && a.Slot.StartAt < from)
            .Select(a => new
            {
                a.AppointmentId, a.AppointmentNo, a.QrToken, a.Status, SlotStart = a.Slot.StartAt, SlotEnd = a.Slot.EndAt,
                Plate = a.Truck != null ? a.Truck.PlateNormalized : null,
                Containers = a.Items.Where(i => i.ContainerNo != null).Select(i => i.ContainerNo!).ToList(),
                HasDriver = a.DriverId != null,
                DriverMobile = a.Driver != null ? a.Driver.Mobile : null,
                TruckBlocked = a.Truck != null && a.Truck.IsBlocked,
                DriverBlocked = a.Driver != null && a.Driver.IsBlocked,
                a.TrafficLight, a.TrafficReason,
            })
            .ToListAsync(ct);

        var all = appointments.Concat(inside).ToList();
        var mobiles = all.Where(a => a.DriverMobile != null).Select(a => a.DriverMobile!).Distinct().ToList();

        return new EdgeSnapshot(
            terminalId, terminal.Code, terminal.Name, terminal.TimeZone, settings, now, to,
            lanes.Select(l => new EdgeLane(l.LaneCode, l.Direction, l.Mode, l.State, l.IsActive)).ToList(),
            devices.Where(d => laneCodes.ContainsKey(d.LaneId)).Select(d => new EdgeDevice(d.DeviceCode, laneCodes[d.LaneId], d.Kind, d.KeyHash!)).ToList(),
            all.Select(a => new EdgeAppointment(a.AppointmentId, a.AppointmentNo, a.QrToken, a.Status, a.SlotStart, a.SlotEnd, a.Plate,
                a.Containers, a.HasDriver, a.DriverMobile, a.TruckBlocked, a.DriverBlocked, a.TrafficLight, a.TrafficReason)).ToList(),
            await passes.ForEdgeAsync(terminalId, mobiles, ct));
    }

    /// <summary>Records the passages the agent decided on its own, oldest first; each one stands alone.</summary>
    public async Task<IReadOnlyList<EdgePassageResult>> RecordAsync(CurrentUser agent, int terminalId, IReadOnlyList<EdgePassage> passages, CancellationToken ct = default)
    {
        var results = new List<EdgePassageResult>();
        foreach (var p in passages.OrderBy(p => p.DecidedAt))
        {
            try
            {
                results.Add(await gate.RecordOfflineAsync(agent, terminalId, p, ct));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                db.ChangeTracker.Clear();
                logger.LogError(ex, "Edge passage {PassageId} at terminal {TerminalId} could not be recorded", p.PassageId, terminalId);
                results.Add(new EdgePassageResult(p.PassageId, EdgePassageOutcome.Failed, "Could not be recorded; see the server log."));
            }
        }
        var applied = results.Count(r => r.Outcome == EdgePassageOutcome.Applied);
        if (results.Count > 0)
            logger.LogInformation("Edge: {Applied} of {Count} passages recorded at terminal {TerminalId}", applied, results.Count, terminalId);
        return results;
    }
}
