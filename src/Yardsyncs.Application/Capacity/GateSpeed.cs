namespace Yardsyncs.Application.Capacity;

/// <summary>How quickly trucks usually get through the gate in an hour, from the terminal's history.</summary>
public enum GateSpeedLevel
{
    /// <summary>Usually well under the terminal's target wait: a good hour to book.</summary>
    Fast,

    /// <summary>Around the target.</summary>
    Normal,

    /// <summary>Usually over the target: trucks queue.</summary>
    Slow,
}

/// <param name="WaitMinutes">The usual (median) minutes from arrival to gate-in.</param>
/// <param name="Sample">Visits the figure comes from.</param>
public sealed record HourSpeed(int WaitMinutes, GateSpeedLevel Level, int Sample);

/// <summary>A visit with its arrival and gate-in, for the gate speed.</summary>
public sealed record SpeedVisit(DateTimeOffset SlotStart, DateTimeOffset ArrivedAt, DateTimeOffset GateInAt);

/// <summary>
/// The usual gate wait per weekday and hour (local time), so booking can show which hours get through quickly
/// (AI-optimised slots, step 2). An hour with too few visits of its own weekday falls back to the same hour on
/// every day; with too little history there is no figure at all, never a guess.
/// </summary>
public sealed class GateSpeedProfile
{
    public const int MinSamples = 12;

    /// <summary>At or under this share of the target, an hour is fast.</summary>
    public const double FastShare = 0.5;

    private readonly Dictionary<(DayOfWeek Day, int Hour), HourSpeed> byDayHour;
    private readonly Dictionary<int, HourSpeed> byHour;

    public static readonly GateSpeedProfile Empty = new(new Dictionary<(DayOfWeek, int), HourSpeed>(), new Dictionary<int, HourSpeed>());

    private GateSpeedProfile(Dictionary<(DayOfWeek, int), HourSpeed> byDayHour, Dictionary<int, HourSpeed> byHour)
    {
        this.byDayHour = byDayHour;
        this.byHour = byHour;
    }

    public bool IsEmpty => byDayHour.Count == 0 && byHour.Count == 0;

    public static GateSpeedProfile From(IReadOnlyList<SpeedVisit> visits, TimeZoneInfo timeZone, int targetWaitMinutes)
    {
        var waits = visits
            .Where(v => v.GateInAt >= v.ArrivedAt && v.GateInAt - v.ArrivedAt < TimeSpan.FromHours(12))
            .Select(v =>
            {
                var local = TimeZoneInfo.ConvertTime(v.SlotStart, timeZone).DateTime;
                return (local.DayOfWeek, local.Hour, Minutes: (v.GateInAt - v.ArrivedAt).TotalMinutes);
            })
            .ToList();

        HourSpeed? Speed(IEnumerable<double> minutes)
        {
            var list = minutes.Order().ToList();
            if (list.Count < MinSamples)
                return null;
            var median = list.Count % 2 == 1 ? list[list.Count / 2] : (list[list.Count / 2 - 1] + list[list.Count / 2]) / 2;
            var wait = (int)Math.Round(median);
            var level = wait <= targetWaitMinutes * FastShare ? GateSpeedLevel.Fast
                : wait > targetWaitMinutes ? GateSpeedLevel.Slow
                : GateSpeedLevel.Normal;
            return new HourSpeed(wait, level, list.Count);
        }

        var byDayHour = waits.GroupBy(w => (w.DayOfWeek, w.Hour))
            .Select(g => (g.Key, Speed: Speed(g.Select(x => x.Minutes))))
            .Where(x => x.Speed is not null)
            .ToDictionary(x => x.Key, x => x.Speed!);
        var byHour = waits.GroupBy(w => w.Hour)
            .Select(g => (g.Key, Speed: Speed(g.Select(x => x.Minutes))))
            .Where(x => x.Speed is not null)
            .ToDictionary(x => x.Key, x => x.Speed!);
        return new GateSpeedProfile(byDayHour, byHour);
    }

    /// <summary>The usual speed for a slot starting at this local time, or null without enough history.</summary>
    public HourSpeed? For(DateTime localStart) =>
        byDayHour.TryGetValue((localStart.DayOfWeek, localStart.Hour), out var speed) ? speed
        : byHour.TryGetValue(localStart.Hour, out var hour) ? hour
        : null;
}
