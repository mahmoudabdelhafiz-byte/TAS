using Yardsyncs.Application.Reports;

namespace Yardsyncs.Application.Tests;

public class ReportDigestTests
{
    private static KpiReport Report(DateOnly from, DateOnly to, KpiTotals totals, params KpiHour[] hours) => new(from, to, totals, [], hours);

    private static readonly KpiTotals Yesterday = new(200, 150, 0.75, 140, 120, 12, 8, 3, 0.1, 0.82, 0.02, 46.4, 6.2);
    private static readonly KpiTotals Today = new(220, 99, 0.45, 99, 0, 0, 0, 0, null, null, 0, null, null);

    [Fact]
    public void The_daily_report_gives_yesterday_and_today_in_english()
    {
        var (subject, body) = ReportDigest.Daily("en", "Demo Terminal",
            Report(new(2026, 10, 17), new(2026, 10, 17), Yesterday),
            Report(new(2026, 10, 18), new(2026, 10, 18), Today, new KpiHour(8, 20, 19, 0.95, 19, null), new KpiHour(9, 20, 5, 0.25, 5, null)));
        Assert.Equal("Daily report Sat 17 Oct", subject);
        Assert.Contains("- Appointments: 140 (120 completed, 8 cancelled)", body);
        Assert.Contains("- No-shows: 12 (10%)", body);
        Assert.Contains("- Turn time: 46 min, waiting at the gate: 6 min", body);
        Assert.Contains("- Walk-ins: 3", body);
        Assert.Contains("- Booked: 99 of 220 (45%)", body);
        Assert.Contains("- Busiest hour: 08:00 (95%)", body);
    }

    [Fact]
    public void The_weekly_report_names_the_companies_with_the_most_no_shows_in_arabic()
    {
        var rows = new[]
        {
            new ReportRow("8", "Gulf Haulage", 64, 51, 11, 2, 0.177, 0.71, 58, 9),
            new ReportRow("7", "Al Noor Transport", 122, 110, 6, 6, 0.05, 0.88, 47, 6),
            new ReportRow("9", "Red Sea Logistics", 31, 30, 0, 0, 0, 0.93, 41, 5),
        };
        var (subject, body) = ReportDigest.Weekly("ar", "Demo Terminal", Report(new(2026, 10, 11), new(2026, 10, 17), Yesterday), rows);
        Assert.Equal("التقرير الأسبوعي الأحد 11 أكتوبر – السبت 17 أكتوبر", subject);
        Assert.Contains("الأكثر في عدم الحضور:", body);
        Assert.Contains("- Gulf Haulage: 11 (18%) من 64 موعد", body);
        Assert.DoesNotContain("Red Sea", body);
    }
}
