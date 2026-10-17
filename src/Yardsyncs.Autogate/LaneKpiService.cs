using Microsoft.EntityFrameworkCore;
using Yardsyncs.Application.Gate;
using Yardsyncs.Application.Reports;
using Yardsyncs.Domain.Gate;

namespace Yardsyncs.Autogate;

/// <summary>Lane KPIs for local dates from..to: transaction time, trucks without a clerk, exceptions and their reasons, OCR read rate.</summary>
public sealed class LaneKpiService(YardsyncsDbContext db)
{
    public async Task<LaneKpiReport?> ReportAsync(int terminalId, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var terminal = await db.Terminals.AsNoTracking().Where(t => t.TerminalId == terminalId).Select(t => new { t.TimeZone }).SingleOrDefaultAsync(ct);
        if (terminal is null)
            return null;
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(terminal.TimeZone);
        // A day either side covers every time zone; the calculator keeps the local dates asked for.
        var start = new DateTimeOffset(from.AddDays(-1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var end = new DateTimeOffset(to.AddDays(2).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        var lanes = await db.GateLanes.AsNoTracking().Where(l => l.TerminalId == terminalId).ToListAsync(ct);
        var laneIds = lanes.Select(l => l.LaneId).ToList();
        var devices = (await db.LaneDevices.AsNoTracking().Where(d => laneIds.Contains(d.LaneId) && d.IsActive).ToListAsync(ct)).ToLookup(d => d.LaneId);
        bool Reads(int laneId, LaneRead read) => devices[laneId].Any(d => LaneRules.ParseReads(d.Reads).Contains(read));

        var transactions = await db.LaneTransactions.AsNoTracking()
            .Where(t => t.TerminalId == terminalId && t.StartedAt >= start && t.StartedAt < end && t.Status != LaneTransactionStatus.Open)
            .Select(t => new KpiLaneTransaction(
                t.LaneId, t.Status, t.StartedAt, t.DecidedAt, t.ClosedAt,
                t.Plate != null, t.PlateCorrected, t.Containers != null, t.ContainersCorrected, t.Reason))
            .ToListAsync(ct);

        return LaneKpiCalculator.Compute(from, to,
            lanes.Select(l => new KpiLane(l.LaneId, l.LaneCode, l.Direction, l.Mode, Reads(l.LaneId, LaneRead.Plate), Reads(l.LaneId, LaneRead.Container))).ToList(),
            transactions, timeZone);
    }
}
