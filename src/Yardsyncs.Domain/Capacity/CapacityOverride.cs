namespace Yardsyncs.Domain.Capacity;

public enum OverrideAction
{
    /// <summary>Replace the capacity with <see cref="CapacityOverride.Value"/>.</summary>
    Set,

    /// <summary>Add <see cref="CapacityOverride.Value"/> (may be negative).</summary>
    Adjust,

    /// <summary>Close the slot (or the category) entirely.</summary>
    Close,
}

/// <summary>An exception to the template for specific days/hours, e.g. a vessel peak or crane maintenance.</summary>
public sealed class CapacityOverride
{
    public int OverrideId { get; set; }
    public int TerminalId { get; set; }
    public DateTimeOffset StartAt { get; set; }
    public DateTimeOffset EndAt { get; set; }

    /// <summary>Null = applies to the slot total; otherwise to that category's quota.</summary>
    public int? CategoryId { get; set; }

    public OverrideAction Action { get; set; }
    public int? Value { get; set; }
    public string Reason { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public int CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public bool Overlaps(DateTimeOffset start, DateTimeOffset end) => StartAt < end && EndAt > start;
}

/// <summary>Unused quota of a category opens to every category this many hours before slot start.</summary>
public sealed class QuotaReleaseRule
{
    public int TerminalId { get; set; }
    public int CategoryId { get; set; }
    public int HoursBefore { get; set; }
}

/// <summary>Capacity held back for a priority company, or for walk-ins/emergencies when CompanyId is null.</summary>
public sealed class CapacityReservation
{
    public int ReservationId { get; set; }
    public int TerminalId { get; set; }
    public int? CompanyId { get; set; }
    public int? CategoryId { get; set; }
    public DateTimeOffset StartAt { get; set; }
    public DateTimeOffset EndAt { get; set; }
    public int UnitsPerSlot { get; set; }

    /// <summary>The reserve opens to everyone this many hours before the slot starts if unused.</summary>
    public int ReleaseHoursBefore { get; set; }

    public bool IsActive { get; set; } = true;
}
