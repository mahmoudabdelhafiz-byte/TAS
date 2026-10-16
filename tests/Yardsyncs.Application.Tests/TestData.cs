using Yardsyncs.Application.Capacity;
using Yardsyncs.Domain.Capacity;

namespace Yardsyncs.Application.Tests;

/// <summary>Builders for capacity configurations. Terminal is in Asia/Riyadh (UTC+3, no DST).</summary>
internal static class TestData
{
    public const int Import = 1;
    public const int Export = 2;
    public const int Empty = 3;

    // 2026-10-20 is a Tuesday.
    public static readonly DateOnly Tuesday = new(2026, 10, 20);

    public static DateTimeOffset Riyadh(int year, int month, int day, int hour, int minute = 0) =>
        new(year, month, day, hour, minute, 0, TimeSpan.FromHours(3));

    public static CapacityTemplateSlot Window(
        DayOfWeek day, int startHour, int endHour, int total, decimal overbookPct = 0, (int Category, int Max)[]? quotas = null) =>
        new()
        {
            Day = day,
            StartTime = new TimeOnly(startHour, 0),
            EndTime = endHour == 24 ? new TimeOnly(23, 59) : new TimeOnly(endHour, 0),
            TotalCapacity = total,
            OverbookPct = overbookPct,
            Quotas = (quotas ?? Array.Empty<(int Category, int Max)>()).Select(q => new CapacityTemplateQuota { CategoryId = q.Category, MaxUnits = q.Max }).ToList(),
        };

    public static CapacityTemplate Default(params CapacityTemplateSlot[] slots) =>
        new() { TemplateId = 1, Name = "Default", IsDefault = true, Slots = slots.ToList() };

    public static CapacityTemplate Seasonal(int id, DateOnly from, DateOnly to, int priority, params CapacityTemplateSlot[] slots) =>
        new() { TemplateId = id, Name = $"Seasonal {id}", ValidFrom = from, ValidTo = to, Priority = priority, Slots = slots.ToList() };

    private static int _overrideId;

    public static CapacityOverride Override(
        DateTimeOffset start, DateTimeOffset end, OverrideAction action, int? value = null, int? category = null, int createdMinute = 0) =>
        new()
        {
            OverrideId = Interlocked.Increment(ref _overrideId),
            StartAt = start,
            EndAt = end,
            Action = action,
            Value = value,
            CategoryId = category,
            Reason = "test",
            CreatedAt = new DateTimeOffset(2026, 1, 1, 0, createdMinute, 0, TimeSpan.Zero),
        };

    public static CapacityConfiguration Config(
        IEnumerable<CapacityTemplate> templates,
        IEnumerable<CapacityOverride>? overrides = null,
        int slotMinutes = 60,
        IEnumerable<int>? categories = null) =>
        new(
            TerminalId: 1,
            TimeZoneId: "Asia/Riyadh",
            SlotMinutes: slotMinutes,
            Templates: templates.ToList(),
            Overrides: (overrides ?? Enumerable.Empty<CapacityOverride>()).ToList(),
            ActiveCategoryIds: (categories ?? new[] { Import, Export, Empty }).ToList());
}
