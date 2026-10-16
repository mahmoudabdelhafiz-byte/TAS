using Yardsyncs.Application.Reports;

namespace Yardsyncs.Application.Tests;

public class YardKpiCalculatorTests
{
    private static readonly TimeZoneInfo Riyadh = TimeZoneInfo.FindSystemTimeZoneById("Asia/Riyadh");
    private static readonly DateOnly Day1 = new(2026, 10, 20);
    private static readonly DateOnly Day2 = new(2026, 10, 21);

    private static DateTimeOffset At(DateOnly day, int hour, int minute = 0) =>
        new(day.ToDateTime(new TimeOnly(hour, minute)), TimeSpan.FromHours(3));

    /// <summary>A stop; times are minutes after the visit's 10:00 gate-in.</summary>
    private static KpiStop Stop(long visit, DateOnly day, int sequence, string block, int? arrived, int? done, int? gateOut = null)
    {
        var gateIn = At(day, 10);
        return new KpiStop(visit, At(day, 9), gateIn, gateOut is null ? null : gateIn.AddMinutes(gateOut.Value), sequence, block,
            arrived is null ? null : gateIn.AddMinutes(arrived.Value), done is null ? null : gateIn.AddMinutes(done.Value));
    }

    [Fact]
    public void Gate_to_block_at_the_block_and_to_the_exit()
    {
        var stops = new[]
        {
            // Visit 1, day 1: drop at E1 (6 min after gate-in, 10 min there), pickup at B2 (4 min drive, 20 min there), out 5 min later.
            Stop(1, Day1, 1, "E1", 6, 16, gateOut: 45),
            Stop(1, Day1, 2, "B2", 20, 40, gateOut: 45),
            // Visit 2, day 1: B2 reached after 10 min, 30 min there.
            Stop(2, Day1, 1, "b2", 10, 40),
            // Visit 3, day 2: confirmed without the app seeing the arrival — no times, but counted.
            Stop(3, Day2, 1, "A1", 25, 25),
        };

        var yard = YardKpiCalculator.Compute(Day1, Day2, stops, Riyadh);

        Assert.Equal(3, yard.Visits);
        Assert.Equal(4, yard.Stops);
        Assert.Equal(0.6667, yard.AppSeenShare);
        Assert.Equal(8, yard.AvgToBlockMinutes);        // (6 + 10) / 2
        Assert.Equal(20, yard.AvgAtBlockMinutes);       // (10 + 20 + 30) / 3
        Assert.Equal(5, yard.AvgToExitMinutes);

        Assert.Equal(new YardDay(Day1, 2, 8, 20), yard.Days[0]);
        Assert.Equal(new YardDay(Day2, 1, null, null), yard.Days[1]);

        // Slowest block first; drive times from the gate or the previous stop.
        Assert.Equal(new[] { "B2", "E1", "A1" }, yard.Blocks.Select(b => b.Block).ToArray());
        var b2 = yard.Blocks[0];
        Assert.Equal(2, b2.Stops);
        Assert.Equal(25, b2.AvgAtBlockMinutes);         // (20 + 30) / 2
        Assert.Equal(7, b2.AvgDriveMinutes);            // (20 - 16 = 4, 10) / 2
        Assert.Null(yard.Blocks[2].AvgAtBlockMinutes);
    }

    [Fact]
    public void No_stops_no_numbers()
    {
        var yard = YardKpiCalculator.Compute(Day1, Day1, [], Riyadh);
        Assert.Equal(0, yard.Visits);
        Assert.Null(yard.AppSeenShare);
        Assert.Null(yard.AvgToBlockMinutes);
        Assert.Empty(yard.Blocks);
    }
}
