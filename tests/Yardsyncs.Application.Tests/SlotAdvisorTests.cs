using Yardsyncs.Application.Capacity;
using Yardsyncs.Domain.Booking;

namespace Yardsyncs.Application.Tests;

public class SlotAdvisorTests
{
    private static readonly TimeZoneInfo Riyadh = TimeZoneInfo.FindSystemTimeZoneById("Asia/Riyadh");

    // Sunday 18 October 2026, 10:00 in Riyadh.
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 10, 0, 0, TimeSpan.FromHours(3));
    private static readonly DateOnly Today = new(2026, 10, 18);

    private static readonly AdviceSettings On = new(Mode: 1, TargetWaitMinutes: 20, MaxOverbookPct: 15, HistoryWeeks: 8);

    private static DateTimeOffset At(DateOnly day, int hour) => new(day.ToDateTime(new TimeOnly(hour, 0)), TimeSpan.FromHours(3));

    private static readonly AdviceWindow SundayMorning = new(DayOfWeek.Sunday, new TimeOnly(6, 0), new TimeOnly(13, 0), 40, 5);
    private static readonly AdviceWindow MondayEvening = new(DayOfWeek.Monday, new TimeOnly(14, 0), TimeOnly.MinValue, 40, 0);

    /// <summary>
    /// Eight weeks: every past day has 10 visits at 08:00 and 10 at 15:00. Sundays at 08:00: <paramref name="sundayNoShows"/>
    /// of the 10 don't show; Mondays at 15:00 wait <paramref name="mondayWait"/> minutes at the gate.
    /// </summary>
    private static List<AdviceVisit> History(int sundayNoShows = 0, int mondayWait = 10, int days = 56)
    {
        var visits = new List<AdviceVisit>();
        for (var d = Today.AddDays(-days); d < Today; d = d.AddDays(1))
        {
            foreach (var hour in new[] { 8, 15 })
            {
                for (var i = 0; i < 10; i++)
                {
                    var start = At(d, hour);
                    var noShow = d.DayOfWeek == DayOfWeek.Sunday && hour == 8 && i < sundayNoShows;
                    var wait = d.DayOfWeek == DayOfWeek.Monday && hour == 15 ? mondayWait : 10;
                    visits.Add(noShow
                        ? new AdviceVisit(start, AppointmentStatus.NoShow, null, null)
                        : new AdviceVisit(start, AppointmentStatus.Completed, start.AddMinutes(5), start.AddMinutes(5 + wait)));
                }
            }
        }
        return visits;
    }

    private static List<AdviceSlot> Slots(DateOnly from, DateOnly to, int capacity, int booked)
    {
        var slots = new List<AdviceSlot>();
        for (var d = from; d <= to; d = d.AddDays(1))
            foreach (var hour in new[] { 8, 15 })
                slots.Add(new AdviceSlot(At(d, hour), capacity, booked, false));
        return slots;
    }

    private static SlotAdvice Run(List<AdviceVisit> history, List<AdviceSlot>? past = null, List<AdviceSlot>? coming = null, AdviceSettings? settings = null) =>
        SlotAdvisor.Compute([SundayMorning, MondayEvening], history, past ?? new List<AdviceSlot>(), coming ?? Slots(Today.AddDays(1), Today.AddDays(14), 40, 0),
            settings ?? On, Riyadh, Now);

    [Fact]
    public void Too_little_history_makes_no_suggestions()
    {
        var advice = Run(History(sundayNoShows: 3, days: 20));
        Assert.False(advice.EnoughHistory);
        Assert.Equal(20, advice.HistoryDays);
        Assert.Empty(advice.Suggestions);
    }

    [Fact]
    public void Off_makes_no_suggestions()
    {
        var advice = Run(History(sundayNoShows: 3), settings: On with { Mode = 0 });
        Assert.Empty(advice.Suggestions);
    }

    [Fact]
    public void Overbooking_follows_the_no_shows_of_the_window()
    {
        // 3 of 10 on Sundays at 08:00 don't show: 30% × 0.8 = 24%, capped at 15%.
        var advice = Run(History(sundayNoShows: 3));
        var s = Assert.Single(advice.Suggestions, x => x.Kind == AdviceKind.Overbook);
        Assert.Equal("overbook|Sunday|06:00", s.Key);
        Assert.Equal(DayOfWeek.Sunday, s.Day);
        Assert.Equal("13:00", s.End);
        Assert.Equal(5m, s.Current);
        Assert.Equal(15m, s.Suggested);
        Assert.Equal(0.3, s.NoShowRate!.Value, 3);
        Assert.Equal(80, s.Sample); // 8 Sundays × 10

        // 1 of 10: 10% × 0.8 = 8%, 3 points over the 5% set: suggested. No no-shows on Monday evening, 0% set: nothing.
        Assert.Equal(8m, Run(History(sundayNoShows: 1)).Suggestions.Single(x => x.Kind == AdviceKind.Overbook).Suggested);
        Assert.DoesNotContain(Run(History(sundayNoShows: 0)).Suggestions, x => x.Kind == AdviceKind.Overbook && x.Day == DayOfWeek.Monday);
    }

    [Fact]
    public void Long_gate_waits_suggest_fewer_trucks_per_slot()
    {
        // Mondays after 14:00 wait 40 minutes against a 20-minute target: 40 × max(0.7, 20/40) = 28 trucks.
        var advice = Run(History(mondayWait: 40));
        var s = Assert.Single(advice.Suggestions, x => x.Kind == AdviceKind.LowerCapacity);
        Assert.Equal("capacity|Monday|14:00", s.Key);
        Assert.Equal("24:00", s.End);
        Assert.Equal(40m, s.Current);
        Assert.Equal(28m, s.Suggested);
        Assert.Equal(40, s.AvgWaitMinutes!.Value, 3);
    }

    [Fact]
    public void Full_windows_with_short_waits_suggest_more_trucks()
    {
        var full = Slots(Today.AddDays(-56), Today.AddDays(-1), 40, 38); // 95% booked
        var advice = Run(History(mondayWait: 5), past: full);
        var s = Assert.Single(advice.Suggestions, x => x.Kind == AdviceKind.RaiseCapacity && x.Day == DayOfWeek.Monday);
        Assert.Equal(44m, s.Suggested);
        Assert.Equal(0.95, s.Utilization!.Value, 3);

        // Short waits but half empty: nothing to raise.
        Assert.DoesNotContain(Run(History(mondayWait: 5), past: Slots(Today.AddDays(-56), Today.AddDays(-1), 40, 20)).Suggestions,
            x => x.Kind == AdviceKind.RaiseCapacity);
    }

    [Fact]
    public void Coming_days_over_capacity_are_flagged()
    {
        // 20 visits a day in the history; next Tuesday has only 16 places (two slots of 8): busy.
        var coming = Slots(Today.AddDays(1), Today.AddDays(14), 40, 0)
            .Select(s => TimeZoneInfo.ConvertTime(s.StartAt, Riyadh).Date == new DateTime(2026, 10, 20) ? s with { Capacity = 8 } : s)
            .ToList();
        var advice = Run(History(), coming: coming);
        var s = Assert.Single(advice.Suggestions, x => x.Kind == AdviceKind.BusyDay);
        Assert.Equal("busy|2026-10-20", s.Key);
        Assert.Equal(new DateOnly(2026, 10, 20), s.Date);
        Assert.Equal(20, s.Forecast);
        Assert.Equal(16m, s.Current);
        Assert.Equal(21m, s.Suggested); // 20 × 1.05, rounded up
        Assert.Equal(8, s.Sample); // eight Tuesdays

        // Busy days come first, then the windows.
        Assert.Equal(AdviceKind.BusyDay, Run(History(sundayNoShows: 3), coming: coming).Suggestions[0].Kind);
    }

    [Fact]
    public void Cancelled_and_future_visits_are_not_counted()
    {
        var history = History(sundayNoShows: 3);
        history.Add(new AdviceVisit(Now.AddHours(1), AppointmentStatus.Confirmed, null, null));
        history.Add(new AdviceVisit(At(Today.AddDays(-7), 8), AppointmentStatus.Cancelled, null, null));
        Assert.Equal(56 * 20, Run(history).Visits);
    }

    [Fact]
    public void Window_times_parse_with_midnight_as_the_end_of_the_day()
    {
        Assert.Equal(TimeOnly.MinValue, SlotAdvisor.ParseTime("24:00"));
        Assert.Equal(new TimeOnly(6, 30), SlotAdvisor.ParseTime("06:30"));
        Assert.True(MondayEvening.Contains(new DateTime(2026, 10, 19, 23, 30, 0)));
        Assert.False(MondayEvening.Contains(new DateTime(2026, 10, 19, 13, 59, 0)));
        Assert.False(SundayMorning.Contains(new DateTime(2026, 10, 18, 13, 0, 0)));
    }
}
