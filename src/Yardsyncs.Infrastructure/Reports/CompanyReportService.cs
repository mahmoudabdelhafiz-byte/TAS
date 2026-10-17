using Microsoft.EntityFrameworkCore;
using Yardsyncs.Application.Languages;
using Yardsyncs.Application.Reports;
using Yardsyncs.Domain;
using Yardsyncs.Domain.Booking;
using Yardsyncs.Domain.Gate;
using Yardsyncs.Infrastructure.Persistence;

namespace Yardsyncs.Infrastructure.Reports;

/// <summary>
/// Reports per trucking company at a terminal: one company's performance (for the company itself and the terminal),
/// and all companies side by side (for the terminal).
/// </summary>
public sealed class CompanyReportService(YardsyncsDbContext db, TimeProvider clock)
{
    /// <exception cref="KeyNotFoundException">Unknown terminal.</exception>
    public async Task<CompanyReport?> ReportAsync(int terminalId, int companyId, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var company = await db.Companies.AsNoTracking().Where(c => c.CompanyId == companyId).Select(c => c.Name).SingleOrDefaultAsync(ct);
        if (company is null)
            return null;
        var (timeZone, settings, start, end) = await PeriodAsync(terminalId, from, to, ct);
        var walkIns = await WalkInsAsync(terminalId, ct);

        var rows = await db.Appointments.AsNoTracking()
            .Where(a => a.TerminalId == terminalId && a.CompanyId == companyId && a.Slot.StartAt >= start && a.Slot.StartAt < end)
            .Select(a => new
            {
                a.AppointmentId, a.Slot.StartAt, a.Slot.EndAt, a.Status, a.ArrivedAt, a.GateInAt, a.GateOutAt,
                Plate = a.Truck != null ? a.Truck.PlateNumber : null, a.DriverId, Driver = a.Driver != null ? a.Driver.FullName : null,
            })
            .ToListAsync(ct);
        var visits = rows.Select(v => new CompanyVisit(
            new KpiVisit(v.StartAt, v.EndAt, v.Status, v.ArrivedAt, v.GateInAt, v.GateOutAt, walkIns.Contains(v.AppointmentId)),
            v.Plate, v.DriverId, v.Driver)).ToList();
        var now = clock.GetUtcNow();
        var (totals, days, trucks, drivers) = CompanyReportCalculator.Compute(from, to, visits, timeZone,
            settings.EarlyGraceMinutes, settings.LateGraceMinutes, now);

        // Holds: visits whose checks failed in the period, by check, with the latest reason (in the reader's language).
        var ids = rows.Select(r => r.AppointmentId).ToList();
        var fails = await db.ValidationResults.AsNoTracking()
            .Where(v => v.Result == CheckResult.Fail && v.CheckType != CheckType.TosUnavailable)
            .Join(db.AppointmentItems.Where(i => ids.Contains(i.AppointmentId)), v => v.ItemId, i => i.ItemId,
                (v, i) => new { i.AppointmentId, v.CheckType, v.Message, v.CheckedAt })
            .ToListAsync(ct);
        var holds = fails.GroupBy(f => f.CheckType)
            .Select(g => new HoldCount(g.Key, g.Select(f => f.AppointmentId).Distinct().Count(),
                ServerText.TOrNull(g.OrderByDescending(f => f.CheckedAt).First().Message)))
            .OrderByDescending(h => h.Visits)
            .ToList();

        var charges = await db.AppointmentCharges.AsNoTracking()
            .Where(c => c.TerminalId == terminalId && c.CompanyId == companyId && c.WaivedAt == null && ids.Contains(c.AppointmentId))
            .Select(c => new { c.Currency, c.AmountMinor, c.Kind })
            .ToListAsync(ct);
        var chargeTotals = charges.GroupBy(c => c.Currency)
            .Select(g => new ChargeTotal(g.Key, g.Count(), g.Sum(c => c.AmountMinor),
                g.Count(c => c.Kind == ChargeKind.NoShow), g.Count(c => c.Kind == ChargeKind.LateCancel)))
            .ToList();

        return new CompanyReport(companyId, company, from, to, totals, days, trucks, drivers, holds, chargeTotals);
    }

    /// <summary>Every company with visits in the period, the most no-shows first: for the terminal.</summary>
    /// <exception cref="KeyNotFoundException">Unknown terminal.</exception>
    public async Task<IReadOnlyList<ReportRow>> CompaniesAsync(int terminalId, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var (_, settings, start, end) = await PeriodAsync(terminalId, from, to, ct);
        var walkIns = await WalkInsAsync(terminalId, ct);
        var rows = await db.Appointments.AsNoTracking()
            .Where(a => a.TerminalId == terminalId && a.Slot.StartAt >= start && a.Slot.StartAt < end)
            .Select(a => new { a.AppointmentId, a.CompanyId, Company = a.Company.Name, a.Slot.StartAt, a.Slot.EndAt, a.Status, a.ArrivedAt, a.GateInAt, a.GateOutAt })
            .ToListAsync(ct);
        var now = clock.GetUtcNow();
        return rows.GroupBy(r => (r.CompanyId, r.Company))
            .Select(g => CompanyReportCalculator.Row(g.Key.CompanyId.ToString(System.Globalization.CultureInfo.InvariantCulture), g.Key.Company,
                g.Select(v => new KpiVisit(v.StartAt, v.EndAt, v.Status, v.ArrivedAt, v.GateInAt, v.GateOutAt, walkIns.Contains(v.AppointmentId))).ToList(),
                settings.EarlyGraceMinutes, settings.LateGraceMinutes, now))
            .OrderByDescending(r => r.NoShows).ThenByDescending(r => r.Appointments).ThenBy(r => r.Name, StringComparer.Ordinal)
            .ToList();
    }

    private async Task<(TimeZoneInfo TimeZone, TerminalSettings Settings, DateTimeOffset Start, DateTimeOffset End)> PeriodAsync(
        int terminalId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var terminal = await db.Terminals.AsNoTracking().SingleOrDefaultAsync(t => t.TerminalId == terminalId, ct)
                       ?? throw new KeyNotFoundException($"Terminal {terminalId} not found.");
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(terminal.TimeZone);
        DateTimeOffset StartOf(DateOnly d)
        {
            var local = d.ToDateTime(TimeOnly.MinValue);
            return new DateTimeOffset(local, timeZone.GetUtcOffset(local)).ToUniversalTime();
        }
        var settings = TerminalSettings.From(await db.TerminalSettings.AsNoTracking().Where(s => s.TerminalId == terminalId).ToListAsync(ct));
        return (timeZone, settings, StartOf(from), StartOf(to.AddDays(1)));
    }

    private async Task<HashSet<long>> WalkInsAsync(int terminalId, CancellationToken ct) =>
        (await db.GateTroubles.AsNoTracking()
            .Where(t => t.TerminalId == terminalId && t.Resolution == TroubleResolution.WalkIn && t.AppointmentId != null)
            .Select(t => t.AppointmentId!.Value)
            .ToListAsync(ct)).ToHashSet();
}
