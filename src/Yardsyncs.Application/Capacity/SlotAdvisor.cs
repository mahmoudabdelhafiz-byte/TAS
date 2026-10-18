using System.Globalization;
using Yardsyncs.Domain.Booking;

namespace Yardsyncs.Application.Capacity;

/// <summary>A window of the default capacity template (local times; End 00:00 = until midnight).</summary>
public sealed record AdviceWindow(DayOfWeek Day, TimeOnly Start, TimeOnly End, int Capacity, decimal OverbookPct)
{
    public bool Contains(DateTime local) =>
        local.DayOfWeek == Day && TimeOnly.FromDateTime(local) >= Start && (End == TimeOnly.MinValue || TimeOnly.FromDateTime(local) < End);
}

/// <summary>A past appointment: when its slot started, how it ended, and the gate times.</summary>
public sealed record AdviceVisit(DateTimeOffset SlotStart, AppointmentStatus Status, DateTimeOffset? ArrivedAt, DateTimeOffset? GateInAt);

public sealed record AdviceSlot(DateTimeOffset StartAt, int Capacity, int Booked, bool IsClosed);

/// <param name="TargetWaitMinutes">Arrival to gate-in the terminal aims for.</param>
/// <param name="MaxOverbookPct">The highest overbooking a suggestion may propose.</param>
/// <param name="HistoryWeeks">How far back the history is read.</param>
public sealed record AdviceSettings(int Mode, int TargetWaitMinutes, int MaxOverbookPct, int HistoryWeeks);

public enum AdviceKind
{
    /// <summary>Overbooking of a template window from its no-show rate.</summary>
    Overbook,

    /// <summary>Fewer trucks per slot: trucks wait at the gate longer than the target.</summary>
    LowerCapacity,

    /// <summary>More trucks per slot: the window is full and trucks get through quickly.</summary>
    RaiseCapacity,

    /// <summary>A coming day expected over its capacity.</summary>
    BusyDay,
}

/// <summary>
/// One suggestion with the numbers behind it (the screen writes the reason from them, in the reader's language).
/// Window suggestions carry Day / Start / End; a busy day carries Date. Current and Suggested are a percentage for
/// Overbook and trucks per slot (per day for BusyDay) otherwise.
/// </summary>
public sealed record SlotSuggestion(
    string Key, AdviceKind Kind, DayOfWeek? Day, string? Start, string? End, DateOnly? Date,
    decimal Current, decimal Suggested, int Sample,
    double? NoShowRate = null, double? AvgWaitMinutes = null, double? Utilization = null, int? Forecast = null);

/// <param name="HistoryDays">Days with appointments in the history read.</param>
/// <param name="EnoughHistory">At least <see cref="SlotAdvisor.MinHistoryDays"/> days: suggestions are made.</param>
public sealed record SlotAdvice(int Mode, int HistoryDays, int Visits, bool EnoughHistory, IReadOnlyList<SlotSuggestion> Suggestions);

/// <summary>
/// Suggestions for the planner from the terminal's own history: overbooking from real no-shows, trucks per slot
/// from the measured gate wait against a target, and coming days whose expected demand passes capacity.
/// Pure: the planner approves or dismisses each one; nothing changes on its own.
/// </summary>
public static class SlotAdvisor
{
    public const int MinHistoryDays = 28;
    public const int MinWindowVisits = 30;
    public const int MinWaitSamples = 20;
    public const int ForecastDays = 14;
    public const int MinSameWeekdays = 3;

    /// <summary>Part of the no-show rate given back as overbooking, so a quiet week doesn't overfill the gate.</summary>
    public const double NoShowShare = 0.8;

    /// <summary>The smallest change worth proposing: percentage points of overbooking.</summary>
    public const decimal MinOverbookChange = 3;

    public static SlotAdvice Compute(
        IReadOnlyList<AdviceWindow> windows, IReadOnlyList<AdviceVisit> history, IReadOnlyList<AdviceSlot> historySlots,
        IReadOnlyList<AdviceSlot> comingSlots, AdviceSettings settings, TimeZoneInfo timeZone, DateTimeOffset now)
    {
        DateTime Local(DateTimeOffset t) => TimeZoneInfo.ConvertTime(t, timeZone).DateTime;
        DateOnly DayOf(DateTimeOffset t) => DateOnly.FromDateTime(Local(t));

        var past = history.Where(v => v.SlotStart < now && v.Status != AppointmentStatus.Cancelled && v.Status != AppointmentStatus.Draft).ToList();
        var historyDays = past.Select(v => DayOf(v.SlotStart)).Distinct().Count();
        var enough = historyDays >= MinHistoryDays;
        if (settings.Mode == 0 || !enough)
            return new SlotAdvice(settings.Mode, historyDays, past.Count, enough, []);

        var suggestions = new List<SlotSuggestion>();

        // Coming days expected over capacity, from the same weekday in the history (recent weeks weigh more).
        var perDay = past.GroupBy(v => DayOf(v.SlotStart)).ToDictionary(g => g.Key, g => g.Count());
        var today = DayOf(now);
        for (var d = today.AddDays(1); d <= today.AddDays(ForecastDays); d = d.AddDays(1))
        {
            var date = d;
            var same = perDay.Where(p => p.Key.DayOfWeek == date.DayOfWeek && p.Key < today).OrderBy(p => p.Key).Select(p => p.Value).ToList();
            if (same.Count < MinSameWeekdays)
                continue;
            var weights = Enumerable.Range(1, same.Count).ToList();
            var forecast = (int)Math.Round(same.Zip(weights, (n, w) => (double)n * w).Sum() / weights.Sum());
            var capacity = comingSlots.Where(s => !s.IsClosed && DayOf(s.StartAt) == date).Sum(s => s.Capacity);
            if (capacity <= 0 || forecast < capacity * 0.95)
                continue;
            var needed = Math.Max(capacity + 1, (int)Math.Ceiling(forecast * 1.05m));
            suggestions.Add(new SlotSuggestion($"busy|{date:yyyy-MM-dd}", AdviceKind.BusyDay, null, null, null, date,
                capacity, needed, same.Count, Forecast: forecast, Utilization: (double)forecast / capacity));
        }

        foreach (var w in windows.OrderBy(w => w.Day).ThenBy(w => w.Start))
        {
            if (w.Capacity <= 0)
                continue;
            var start = w.Start.ToString("HH:mm", CultureInfo.InvariantCulture);
            var end = w.End == TimeOnly.MinValue ? "24:00" : w.End.ToString("HH:mm", CultureInfo.InvariantCulture);
            var inWindow = past.Where(v => w.Contains(Local(v.SlotStart))).ToList();
            if (inWindow.Count < MinWindowVisits)
                continue;

            var noShowRate = (double)inWindow.Count(v => v.Status == AppointmentStatus.NoShow) / inWindow.Count;
            var suggestedOverbook = Math.Min(settings.MaxOverbookPct, (decimal)Math.Floor(noShowRate * 100 * NoShowShare + 1e-9));
            if (Math.Abs(suggestedOverbook - w.OverbookPct) >= MinOverbookChange)
                suggestions.Add(new SlotSuggestion($"overbook|{w.Day}|{start}", AdviceKind.Overbook, w.Day, start, end, null,
                    w.OverbookPct, suggestedOverbook, inWindow.Count, NoShowRate: noShowRate));

            var waits = inWindow
                .Where(v => v.ArrivedAt is not null && v.GateInAt is not null && v.GateInAt >= v.ArrivedAt)
                .Select(v => (v.GateInAt!.Value - v.ArrivedAt!.Value).TotalMinutes)
                .ToList();
            if (waits.Count < MinWaitSamples)
                continue;
            var avgWait = waits.Average();
            var slots = historySlots.Where(s => !s.IsClosed && s.Capacity > 0 && w.Contains(Local(s.StartAt))).ToList();
            double? utilization = slots.Count == 0 ? null : (double)slots.Sum(s => s.Booked) / slots.Sum(s => s.Capacity);

            if (avgWait > settings.TargetWaitMinutes * 1.25)
            {
                var factor = Math.Clamp(settings.TargetWaitMinutes / avgWait, 0.7, 0.95);
                var lower = Math.Max(1, (int)Math.Floor(w.Capacity * factor));
                if (lower < w.Capacity)
                    suggestions.Add(new SlotSuggestion($"capacity|{w.Day}|{start}", AdviceKind.LowerCapacity, w.Day, start, end, null,
                        w.Capacity, lower, waits.Count, AvgWaitMinutes: avgWait, Utilization: utilization));
            }
            else if (avgWait < settings.TargetWaitMinutes * 0.6 && utilization >= 0.9)
            {
                var higher = Math.Max(w.Capacity + 1, (int)Math.Ceiling(w.Capacity * 1.1m));
                suggestions.Add(new SlotSuggestion($"capacity|{w.Day}|{start}", AdviceKind.RaiseCapacity, w.Day, start, end, null,
                    w.Capacity, higher, waits.Count, AvgWaitMinutes: avgWait, Utilization: utilization));
            }
        }

        return new SlotAdvice(settings.Mode, historyDays, past.Count, enough, suggestions);
    }

    /// <summary>"06:00" / "24:00" as a template window time (24:00 = midnight, the end of the day).</summary>
    public static TimeOnly ParseTime(string value) =>
        value is "24:00" or "23:59" ? TimeOnly.MinValue : TimeOnly.ParseExact(value, "HH:mm", CultureInfo.InvariantCulture);
}
