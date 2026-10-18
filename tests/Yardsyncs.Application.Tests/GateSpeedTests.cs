using Yardsyncs.Application.Capacity;

namespace Yardsyncs.Application.Tests;

public class GateSpeedTests
{
    private static readonly TimeZoneInfo Riyadh = TimeZoneInfo.FindSystemTimeZoneById("Asia/Riyadh");
    private static readonly DateOnly Today = new(2026, 10, 18); // a Sunday

    private static DateTimeOffset At(DateOnly day, int hour) => new(day.ToDateTime(new TimeOnly(hour, 0)), TimeSpan.FromHours(3));

    /// <summary>Visits on <paramref name="weeks"/> past days of the same weekday as <paramref name="from"/>, each waiting <paramref name="wait"/> minutes.</summary>
    private static IEnumerable<SpeedVisit> Visits(DateOnly from, int hour, int perDay, int wait, int weeks = 4, int step = 7)
    {
        for (var w = 1; w <= weeks; w++)
        {
            var day = from.AddDays(-step * w);
            for (var i = 0; i < perDay; i++)
            {
                var start = At(day, hour);
                yield return new SpeedVisit(start, start.AddMinutes(3), start.AddMinutes(3 + wait));
            }
        }
    }

    [Fact]
    public void Hours_are_fast_normal_or_slow_against_the_target()
    {
        var visits = Visits(Today, 7, 4, wait: 6)            // Sundays 07:00 → 6 min
            .Concat(Visits(Today, 9, 4, wait: 15))           // Sundays 09:00 → 15 min
            .Concat(Visits(Today, 11, 4, wait: 45))          // Sundays 11:00 → 45 min
            .ToList();

        var profile = GateSpeedProfile.From(visits, Riyadh, targetWaitMinutes: 20);

        var sunday = Today.ToDateTime(TimeOnly.MinValue);
        Assert.Equal(new HourSpeed(6, GateSpeedLevel.Fast, 16), profile.For(sunday.AddHours(7)));
        Assert.Equal(GateSpeedLevel.Normal, profile.For(sunday.AddHours(9))!.Level);
        Assert.Equal(15, profile.For(sunday.AddHours(9))!.WaitMinutes);
        Assert.Equal(GateSpeedLevel.Slow, profile.For(sunday.AddHours(11))!.Level);
        Assert.Null(profile.For(sunday.AddHours(13)));                 // no history at 13:00
    }

    [Fact]
    public void Too_few_visits_give_no_figure_and_the_median_ignores_one_long_wait()
    {
        var few = Visits(Today, 8, 2, wait: 5).ToList();                // 8 visits: under the minimum
        Assert.True(GateSpeedProfile.From(few, Riyadh, 20).IsEmpty);

        var start = At(Today.AddDays(-7), 8);
        var withOneLong = Visits(Today, 8, 3, wait: 5).Append(new SpeedVisit(start, start, start.AddMinutes(200))).ToList();
        var speed = GateSpeedProfile.From(withOneLong, Riyadh, 20).For(Today.ToDateTime(new TimeOnly(8, 0)));
        Assert.Equal(5, speed!.WaitMinutes);
        Assert.Equal(GateSpeedLevel.Fast, speed.Level);
    }

    [Fact]
    public void A_weekday_without_enough_visits_falls_back_to_the_same_hour_on_every_day()
    {
        // Every day of the last 2 weeks at 10:00, one visit each: 14 visits, but at most 2 per weekday.
        var visits = Visits(Today, 10, 1, wait: 30, weeks: 14, step: 1).ToList();

        var profile = GateSpeedProfile.From(visits, Riyadh, 20);

        var speed = profile.For(Today.AddDays(3).ToDateTime(new TimeOnly(10, 0)));
        Assert.Equal(new HourSpeed(30, GateSpeedLevel.Slow, 14), speed);
    }

    [Fact]
    public void Arrivals_after_gate_in_and_waits_over_twelve_hours_are_left_out()
    {
        var start = At(Today.AddDays(-7), 9);
        var bad = Enumerable.Range(0, 20).Select(_ => new SpeedVisit(start, start.AddMinutes(30), start.AddMinutes(10)))
            .Concat(Enumerable.Range(0, 20).Select(_ => new SpeedVisit(start, start, start.AddHours(13))))
            .ToList();
        Assert.True(GateSpeedProfile.From(bad, Riyadh, 20).IsEmpty);
    }
}
