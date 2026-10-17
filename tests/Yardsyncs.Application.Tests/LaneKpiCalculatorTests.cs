using Yardsyncs.Application.Reports;
using Yardsyncs.Domain.Gate;

namespace Yardsyncs.Application.Tests;

public class LaneKpiCalculatorTests
{
    private static readonly TimeZoneInfo Riyadh = TimeZoneInfo.FindSystemTimeZoneById("Asia/Riyadh");
    private static readonly DateOnly Day = new(2026, 10, 20);
    private static DateTimeOffset At(int hour, int minute = 0, int second = 0) => new(2026, 10, 20, hour, minute, second, TimeSpan.FromHours(3));

    private static readonly KpiLane In1 = new(1, "IN-1", LaneDirection.In, LaneMode.Automatic, ReadsPlates: true, ReadsContainers: true);
    private static readonly KpiLane In2 = new(2, "IN-2", LaneDirection.In, LaneMode.Automatic, ReadsPlates: false, ReadsContainers: false);
    private static readonly KpiLane Out1 = new(3, "OUT-1", LaneDirection.Out, LaneMode.Manual, ReadsPlates: true, ReadsContainers: false);

    /// <summary>A truck through the barrier: decided after <paramref name="decide"/> s, through after <paramref name="through"/> s.</summary>
    private static KpiLaneTransaction Passed(int lane, int hour, int decide, int through, string? reason = null,
        bool plate = true, bool plateFixed = false, bool containers = true, bool containersFixed = false) =>
        new(lane, LaneTransactionStatus.Passed, At(hour), At(hour).AddSeconds(decide), At(hour).AddSeconds(through), plate, plateFixed, containers, containersFixed, reason);

    [Fact]
    public void Times_shares_and_read_rates_per_lane_and_in_total()
    {
        var txs = new List<KpiLaneTransaction>
        {
            Passed(1, 8, 10, 40),
            Passed(1, 9, 12, 50),
            Passed(1, 10, 20, 59 * 60 / 2, reason: "EARLY", plateFixed: true),   // a clerk helped and fixed the plate
            Passed(1, 11, 8, 30, containers: false, containersFixed: true),        // the cameras missed the container
            new(1, LaneTransactionStatus.Abandoned, At(12), null, At(12, 2), false, false, false, false, null),
            new(1, LaneTransactionStatus.Open, At(13), null, null, true, false, false, false, null),   // not done yet: not counted
            Passed(2, 8, 15, 45, plate: false, containers: false),
        };

        var report = LaneKpiCalculator.Compute(Day, Day, [In1, In2, Out1], txs, Riyadh);

        var in1 = report.Lanes.Single(l => l.LaneCode == "IN-1").Kpis;
        Assert.Equal(4, in1.Trucks);
        Assert.Equal(1, in1.Abandoned);
        Assert.Equal(0.75, in1.StraightThroughRate);
        Assert.Equal(0.25, in1.ExceptionRate);
        Assert.Equal(10, in1.MedianDecisionSeconds);            // 8, 10, 12, 20 → nearest rank 2
        Assert.Equal(40, in1.MedianLaneSeconds);                // 30, 40, 50, 1770
        Assert.Equal(1770, in1.P90LaneSeconds);
        Assert.Equal(0.75, in1.PlateReadRate);                  // one plate corrected
        Assert.Equal(0.75, in1.ContainerReadRate);              // one missed and corrected

        // IN-2 reads nothing: no read rates. A manual lane without trucks is left out.
        var in2 = report.Lanes.Single(l => l.LaneCode == "IN-2").Kpis;
        Assert.Null(in2.PlateReadRate);
        Assert.Null(in2.ContainerReadRate);
        Assert.DoesNotContain(report.Lanes, l => l.LaneCode == "OUT-1");

        Assert.Equal(5, report.Totals.Trucks);
        Assert.Equal(0.2, report.Totals.ExceptionRate);
        Assert.Equal(0.75, report.Totals.PlateReadRate);        // only lanes with a plate camera count
        var reason = Assert.Single(report.Reasons);
        Assert.Equal(("EARLY", 1, 1.0), (reason.Reason, reason.Count, reason.Share));
        var day = Assert.Single(report.Days);
        Assert.Equal(5, day.Trucks);
    }

    [Fact]
    public void Days_follow_the_terminals_local_date_and_empty_ranges_have_no_rates()
    {
        // 00:30 local on the 21st is still the 20th in UTC: it belongs to the 21st.
        var late = new KpiLaneTransaction(1, LaneTransactionStatus.Referred, new DateTimeOffset(2026, 10, 21, 0, 30, 0, TimeSpan.FromHours(3)),
            new DateTimeOffset(2026, 10, 21, 0, 30, 20, TimeSpan.FromHours(3)), null, true, false, false, false, "NO_APPOINTMENT");
        var report = LaneKpiCalculator.Compute(Day, Day.AddDays(1), [In1], [late], Riyadh);
        Assert.Equal(0, report.Days[0].Trucks);
        Assert.Null(report.Days[0].ExceptionRate);
        Assert.Equal(1, report.Days[1].Trucks);
        Assert.Equal(1.0, report.Days[1].ExceptionRate);
        Assert.Null(report.Totals.MedianLaneSeconds);           // still at the lane: no time through the barrier

        var none = LaneKpiCalculator.Compute(Day, Day, [In1], [], Riyadh);
        Assert.Equal(0, none.Totals.Trucks);
        Assert.Null(none.Totals.StraightThroughRate);
        Assert.Single(none.Lanes);                              // an automatic lane shows even without trucks
    }

    [Theory]
    [InlineData(0.5, 2)]
    [InlineData(0.9, 5)]
    [InlineData(0.1, 1)]
    public void Percentile_is_the_nearest_rank(double p, double expected) =>
        Assert.Equal(expected, LaneKpiCalculator.Percentile([5, 1, 4, 2, 3], p));
}
