using Tas.Application.Reports;
using Tas.Domain.Booking;

namespace Tas.Application.Tests;

public class KpiCalculatorTests
{
    private static readonly TimeZoneInfo Riyadh = TimeZoneInfo.FindSystemTimeZoneById("Asia/Riyadh");
    private static readonly DateOnly Day1 = new(2026, 10, 20);
    private static readonly DateOnly Day2 = new(2026, 10, 21);

    /// <summary>Local Riyadh time on a day.</summary>
    private static DateTimeOffset At(DateOnly day, int hour, int minute = 0) =>
        new(day.ToDateTime(new TimeOnly(hour, minute)), TimeSpan.FromHours(3));

    private static KpiVisit Visit(DateOnly day, int hour, AppointmentStatus status, int? arrivedMin = null, int? inMin = null, int? outMin = null, bool walkIn = false)
    {
        var start = At(day, hour);
        return new KpiVisit(start, start.AddHours(1), status,
            arrivedMin is null ? null : start.AddMinutes(arrivedMin.Value),
            inMin is null ? null : start.AddMinutes(inMin.Value),
            outMin is null ? null : start.AddMinutes(outMin.Value),
            walkIn);
    }

    [Fact]
    public void Days_hours_and_totals()
    {
        var slots = new[]
        {
            new KpiSlot(At(Day1, 8), 10, 4, false),
            new KpiSlot(At(Day1, 9), 10, 1, false),
            new KpiSlot(At(Day1, 13), 10, 0, true), // closed: not counted
            new KpiSlot(At(Day2, 8), 20, 5, false),
        };
        var visits = new[]
        {
            Visit(Day1, 8, AppointmentStatus.Completed, arrivedMin: 10, inMin: 20, outMin: 70),     // turn 60, wait 10, on time
            Visit(Day1, 8, AppointmentStatus.Completed, arrivedMin: -60, inMin: -50, outMin: 0),    // 60 min early: not on time, turn 60
            Visit(Day1, 8, AppointmentStatus.NoShow),
            Visit(Day1, 9, AppointmentStatus.Cancelled),
            Visit(Day1, 9, AppointmentStatus.Completed, inMin: 5, outMin: 35, walkIn: true),         // no arrival read: turn from gate-in 30
            Visit(Day2, 8, AppointmentStatus.Confirmed),                                             // still upcoming
        };

        var report = KpiCalculator.Compute(Day1, Day2, slots, visits, Riyadh, 30, 30, At(Day2, 6));

        Assert.Equal(2, report.Days.Count);
        var d1 = report.Days[0];
        Assert.Equal(20, d1.Capacity);
        Assert.Equal(5, d1.Booked);
        Assert.Equal(0.25, d1.Utilization);
        Assert.Equal(4, d1.Appointments);
        Assert.Equal(3, d1.Completed);
        Assert.Equal(1, d1.NoShows);
        Assert.Equal(1, d1.Cancelled);
        Assert.Equal(1, d1.WalkIns);
        Assert.Equal(3, d1.Arrived);
        Assert.Equal(2, d1.OnTime);
        Assert.Equal(50, d1.AvgTurnMinutes);
        Assert.Equal(10, d1.AvgWaitMinutes);

        var t = report.Totals;
        Assert.Equal(40, t.Capacity);
        Assert.Equal(10, t.Booked);
        Assert.Equal(5, t.Appointments);
        Assert.Equal(0.25, t.NoShowRate);            // 1 no-show of 4 due (the upcoming one is not due yet)
        Assert.Equal(0.6667, t.OnTimeRate);
        Assert.Equal(0.2, t.WalkInShare);

        Assert.Equal(new[] { 8, 9 }, report.Hours.Select(h => h.Hour).ToArray()); // 13:00 closed and empty
        Assert.Equal(30, report.Hours[0].Capacity);
        Assert.Equal(4, report.Hours[0].Appointments);
    }

    [Fact]
    public void Empty_range_has_no_rates()
    {
        var report = KpiCalculator.Compute(Day1, Day1, Array.Empty<KpiSlot>(), Array.Empty<KpiVisit>(), Riyadh, 30, 30, At(Day1, 12));

        Assert.Single(report.Days);
        Assert.Null(report.Totals.Utilization);
        Assert.Null(report.Totals.NoShowRate);
        Assert.Null(report.Totals.AvgTurnMinutes);
        Assert.Empty(report.Hours);
    }
}
