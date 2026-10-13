using Tas.Domain.Capacity;

namespace Tas.Application.Capacity;

/// <summary>Everything the planner needs for one terminal. Pure data, no database.</summary>
public sealed record CapacityConfiguration(
    int TerminalId,
    string TimeZoneId,
    int SlotMinutes,
    IReadOnlyList<CapacityTemplate> Templates,
    IReadOnlyList<CapacityOverride> Overrides,
    IReadOnlyList<int> ActiveCategoryIds);

/// <summary>The resolved capacity of one slot, before it is written to the database.</summary>
public sealed record PlannedSlot(
    DateTimeOffset StartAt,
    DateTimeOffset EndAt,
    int TotalCapacity,
    bool IsClosed,
    IReadOnlyDictionary<int, int> CategoryQuotas);

/// <summary>
/// Turns capacity rules into concrete slots.
///
/// Precedence (most specific wins):
///   1. default template
///   2. seasonal template covering the date (highest priority) — replaces the default for the whole day
///   3. overrides overlapping the slot — total-level first, then category-level, each in creation order:
///      CLOSE → 0, SET → value, ADJUST → + value. CLOSE on the total closes the slot.
/// Then overbooking is applied: floor(capacity × (1 + overbook% / 100)).
/// A category without an explicit quota is limited only by the slot total.
/// </summary>
public static class SlotPlanner
{
    private static readonly TimeOnly EndOfDayMarker = new(23, 59);

    public static IReadOnlyList<PlannedSlot> Plan(CapacityConfiguration config, DateOnly fromLocalDate, DateOnly toLocalDate)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.SlotMinutes <= 0)
            throw new ArgumentOutOfRangeException(nameof(config), "SlotMinutes must be positive.");
        if (toLocalDate < fromLocalDate)
            return [];

        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(config.TimeZoneId);
        var step = TimeSpan.FromMinutes(config.SlotMinutes);
        var result = new List<PlannedSlot>();
        var seenStarts = new HashSet<DateTimeOffset>();

        for (var date = fromLocalDate; date <= toLocalDate; date = date.AddDays(1))
        {
            var template = SelectTemplate(config.Templates, date);
            if (template is null)
                continue;

            foreach (var window in template.Slots.Where(s => s.Day == date.DayOfWeek).OrderBy(s => s.StartTime))
            {
                var windowEnd = window.EndTime >= EndOfDayMarker
                    ? TimeSpan.FromDays(1)
                    : window.EndTime.ToTimeSpan();

                for (var offset = window.StartTime.ToTimeSpan(); offset + step <= windowEnd; offset += step)
                {
                    var localStart = date.ToDateTime(TimeOnly.FromTimeSpan(offset), DateTimeKind.Unspecified);
                    if (timeZone.IsInvalidTime(localStart))
                        continue; // skipped by a daylight-saving jump

                    var startUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localStart, timeZone), TimeSpan.Zero);

                    // Overlapping windows in a template: the earliest window wins for a given start.
                    if (!seenStarts.Add(startUtc))
                        continue;

                    result.Add(ResolveSlot(window, startUtc, startUtc + step, config));
                }
            }
        }

        result.Sort((a, b) => a.StartAt.CompareTo(b.StartAt));
        return result;
    }

    /// <summary>Highest-priority active seasonal template covering the date, else the active default.</summary>
    public static CapacityTemplate? SelectTemplate(IEnumerable<CapacityTemplate> templates, DateOnly date)
    {
        var active = templates.Where(t => t.IsActive).ToList();

        var seasonal = active
            .Where(t => !t.IsDefault && t.Covers(date))
            .OrderByDescending(t => t.Priority)
            .ThenByDescending(t => t.TemplateId)
            .FirstOrDefault();

        return seasonal ?? active.FirstOrDefault(t => t.IsDefault);
    }

    private static PlannedSlot ResolveSlot(
        CapacityTemplateSlot window, DateTimeOffset start, DateTimeOffset end, CapacityConfiguration config)
    {
        var overrides = config.Overrides
            .Where(o => o.IsActive && o.Overlaps(start, end))
            .OrderBy(o => o.CreatedAt)
            .ThenBy(o => o.OverrideId)
            .ToList();

        // 1. Slot total.
        var total = window.TotalCapacity;
        var closed = false;
        foreach (var o in overrides.Where(o => o.CategoryId is null))
        {
            switch (o.Action)
            {
                case OverrideAction.Close:
                    closed = true;
                    break;
                case OverrideAction.Set:
                    total = o.Value ?? total;
                    break;
                case OverrideAction.Adjust:
                    total += o.Value ?? 0;
                    break;
            }
        }
        total = Math.Max(0, total);

        // 2. Category quotas (explicit ones only; others are bounded by the total).
        var explicitQuotas = window.Quotas.ToDictionary(q => q.CategoryId, q => q.MaxUnits);
        foreach (var o in overrides.Where(o => o.CategoryId is not null))
        {
            var categoryId = o.CategoryId!.Value;
            var current = explicitQuotas.TryGetValue(categoryId, out var q) ? q : total;
            explicitQuotas[categoryId] = o.Action switch
            {
                OverrideAction.Close => 0,
                OverrideAction.Set => o.Value ?? current,
                OverrideAction.Adjust => current + (o.Value ?? 0),
                _ => current,
            };
        }

        // 3. Overbooking.
        var factor = 1m + Math.Max(0m, window.OverbookPct) / 100m;
        var effectiveTotal = closed ? 0 : ApplyFactor(total, factor);

        var categoryQuotas = new Dictionary<int, int>();
        foreach (var categoryId in config.ActiveCategoryIds)
        {
            categoryQuotas[categoryId] = closed
                ? 0
                : explicitQuotas.TryGetValue(categoryId, out var quota)
                    ? Math.Min(ApplyFactor(Math.Max(0, quota), factor), effectiveTotal)
                    : effectiveTotal;
        }

        return new PlannedSlot(start, end, effectiveTotal, closed, categoryQuotas);
    }

    private static int ApplyFactor(int capacity, decimal factor) => (int)Math.Floor(capacity * factor);
}
