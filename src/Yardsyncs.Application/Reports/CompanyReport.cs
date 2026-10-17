using Yardsyncs.Domain.Booking;

namespace Yardsyncs.Application.Reports;

/// <summary>A company's visit for its report: the KPI view plus the truck and driver.</summary>
public sealed record CompanyVisit(KpiVisit Visit, string? TruckPlate, int? DriverId, string? DriverName);

/// <summary>One truck, driver or company in a report.</summary>
/// <param name="Key">The plate, the driver id or the company id, as text.</param>
public sealed record ReportRow(
    string Key, string Name, int Appointments, int Completed, int NoShows, int Cancelled,
    double? NoShowRate, double? OnTimeRate, double? AvgTurnMinutes, double? AvgWaitMinutes);

/// <summary>Visits held by a check (customs, line release, unpaid…) in the period, with the latest reason given.</summary>
public sealed record HoldCount(CheckType Check, int Visits, string? LastReason);

/// <summary>Charges in the period, per currency (waived ones left out).</summary>
public sealed record ChargeTotal(string Currency, int Charges, long AmountMinor, int NoShows, int LateCancels);

/// <summary>How one trucking company did at the terminal: its totals by day, its trucks and drivers, holds and charges.</summary>
public sealed record CompanyReport(
    int CompanyId, string CompanyName, DateOnly From, DateOnly To, KpiTotals Totals, IReadOnlyList<KpiDay> Days,
    IReadOnlyList<ReportRow> Trucks, IReadOnlyList<ReportRow> Drivers, IReadOnlyList<HoldCount> Holds, IReadOnlyList<ChargeTotal> Charges);

public static class CompanyReportCalculator
{
    /// <summary>Days with totals, and one row per truck and per driver (most visits first).</summary>
    public static (KpiTotals Totals, IReadOnlyList<KpiDay> Days, IReadOnlyList<ReportRow> Trucks, IReadOnlyList<ReportRow> Drivers) Compute(
        DateOnly from, DateOnly to, IReadOnlyList<CompanyVisit> visits, TimeZoneInfo timeZone, int early, int late, DateTimeOffset now)
    {
        var all = visits.Select(v => v.Visit).ToList();
        var report = KpiCalculator.Compute(from, to, [], all, timeZone, early, late, now);
        var totals = KpiCalculator.TotalsOf(all, early, late, now);

        var trucks = visits.Where(v => v.TruckPlate is not null)
            .GroupBy(v => v.TruckPlate!)
            .Select(g => Row(g.Key, g.Key, g.Select(v => v.Visit).ToList(), early, late, now))
            .OrderByDescending(r => r.Appointments).ThenBy(r => r.Name, StringComparer.Ordinal)
            .ToList();
        var drivers = visits.Where(v => v.DriverId is not null)
            .GroupBy(v => v.DriverId!.Value)
            .Select(g => Row(g.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), g.First().DriverName ?? "?",
                g.Select(v => v.Visit).ToList(), early, late, now))
            .OrderByDescending(r => r.Appointments).ThenBy(r => r.Name, StringComparer.Ordinal)
            .ToList();
        return (totals, report.Days.Select(d => d with { Capacity = 0, Booked = 0, Utilization = null }).ToList(), trucks, drivers);
    }

    /// <summary>A row from a set of visits.</summary>
    public static ReportRow Row(string key, string name, IReadOnlyList<KpiVisit> visits, int early, int late, DateTimeOffset now)
    {
        var t = KpiCalculator.TotalsOf(visits, early, late, now);
        return new ReportRow(key, name, t.Appointments, t.Completed, t.NoShows, t.Cancelled, t.NoShowRate, t.OnTimeRate, t.AvgTurnMinutes, t.AvgWaitMinutes);
    }
}
