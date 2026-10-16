using Microsoft.EntityFrameworkCore;
using Yardsyncs.Application.Reports;
using Yardsyncs.Domain;
using Yardsyncs.Domain.Gate;
using Yardsyncs.Infrastructure.Persistence;

namespace Yardsyncs.Infrastructure.Reports;

/// <summary>Loads slots, appointments and walk-ins for a date range and computes the terminal KPIs.</summary>
public sealed class KpiService(YardsyncsDbContext db, TimeProvider clock)
{
    /// <exception cref="KeyNotFoundException">Unknown terminal.</exception>
    public async Task<KpiReport> ReportAsync(int terminalId, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var terminal = await db.Terminals.AsNoTracking().SingleOrDefaultAsync(t => t.TerminalId == terminalId, ct)
                       ?? throw new KeyNotFoundException($"Terminal {terminalId} not found.");
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(terminal.TimeZone);

        DateTimeOffset StartOf(DateOnly d)
        {
            var local = d.ToDateTime(TimeOnly.MinValue);
            return new DateTimeOffset(local, timeZone.GetUtcOffset(local)).ToUniversalTime();
        }

        var start = StartOf(from);
        var end = StartOf(to.AddDays(1));

        var slots = await db.Slots.AsNoTracking()
            .Where(s => s.TerminalId == terminalId && s.StartAt >= start && s.StartAt < end)
            .Select(s => new KpiSlot(s.StartAt, s.TotalCapacity, s.TotalBooked, s.IsClosed))
            .ToListAsync(ct);

        var walkIns = await db.GateTroubles.AsNoTracking()
            .Where(t => t.TerminalId == terminalId && t.Resolution == TroubleResolution.WalkIn && t.AppointmentId != null)
            .Select(t => t.AppointmentId!.Value)
            .ToListAsync(ct);

        var visits = await db.Appointments.AsNoTracking()
            .Where(a => a.TerminalId == terminalId && a.Slot.StartAt >= start && a.Slot.StartAt < end)
            .Select(a => new { a.AppointmentId, a.Slot.StartAt, a.Slot.EndAt, a.Status, a.ArrivedAt, a.GateInAt, a.GateOutAt })
            .ToListAsync(ct);

        var walkInSet = walkIns.ToHashSet();
        var settings = TerminalSettings.From(
            await db.TerminalSettings.AsNoTracking().Where(s => s.TerminalId == terminalId).ToListAsync(ct));

        var report = KpiCalculator.Compute(
            from, to, slots,
            visits.Select(v => new KpiVisit(v.StartAt, v.EndAt, v.Status, v.ArrivedAt, v.GateInAt, v.GateOutAt, walkInSet.Contains(v.AppointmentId))).ToList(),
            timeZone, settings.EarlyGraceMinutes, settings.LateGraceMinutes, clock.GetUtcNow());

        // Inside the terminal: the stops the driver app followed.
        var stops = await db.YardInstructions.AsNoTracking()
            .Where(y => y.StopType != "EXIT" && y.Status != "CANCELLED")
            .Join(db.Appointments.Where(a => a.TerminalId == terminalId && a.Slot.StartAt >= start && a.Slot.StartAt < end),
                y => y.AppointmentId, a => a.AppointmentId,
                (y, a) => new KpiStop(a.AppointmentId, a.Slot.StartAt, a.GateInAt, a.GateOutAt, y.Sequence, y.Block, y.ArrivedAt, y.CompletedAt))
            .ToListAsync(ct);
        return stops.Count == 0 ? report : report with { Yard = YardKpiCalculator.Compute(from, to, stops, timeZone) };
    }
}
