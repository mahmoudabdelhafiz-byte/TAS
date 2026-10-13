namespace Tas.Domain.Capacity;

/// <summary>
/// A weekly capacity pattern. Exactly one active default template per terminal (no dates);
/// seasonal templates (Ramadan, summer…) have a date range and replace the default for whole days.
/// </summary>
public sealed class CapacityTemplate
{
    public int TemplateId { get; set; }
    public int TerminalId { get; set; }
    public string Name { get; set; } = "";
    public bool IsDefault { get; set; }
    public DateOnly? ValidFrom { get; set; }
    public DateOnly? ValidTo { get; set; }

    /// <summary>Higher wins when several seasonal templates cover the same date.</summary>
    public int Priority { get; set; }

    public bool IsActive { get; set; } = true;
    public List<CapacityTemplateSlot> Slots { get; set; } = [];

    public bool Covers(DateOnly date) =>
        IsDefault || (ValidFrom is { } first && ValidTo is { } last && first <= date && date <= last);
}

/// <summary>
/// A time window on one weekday inside a template. The generator splits the window into
/// slots of <see cref="Terminal.SlotMinutes"/>; <see cref="TotalCapacity"/> applies to each slot.
/// </summary>
public sealed class CapacityTemplateSlot
{
    public int TemplateSlotId { get; set; }
    public int TemplateId { get; set; }
    public DayOfWeek Day { get; set; }
    public TimeOnly StartTime { get; set; }

    /// <summary>Window end. Use 23:59 to mean "until midnight".</summary>
    public TimeOnly EndTime { get; set; }

    public int TotalCapacity { get; set; }

    /// <summary>Percent added on top of capacity to absorb expected no-shows.</summary>
    public decimal OverbookPct { get; set; }

    public List<CapacityTemplateQuota> Quotas { get; set; } = [];
}

/// <summary>Maximum units for one category inside a template slot.</summary>
public sealed class CapacityTemplateQuota
{
    public int TemplateSlotId { get; set; }
    public int CategoryId { get; set; }
    public int MaxUnits { get; set; }
}
