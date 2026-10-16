namespace Yardsyncs.Domain.Capacity;

/// <summary>
/// A concrete bookable time window, generated from templates and overrides.
/// Bookings consume <see cref="TotalBooked"/> and the per-category counters.
/// </summary>
public sealed class Slot
{
    public long SlotId { get; set; }
    public int TerminalId { get; set; }
    public DateTimeOffset StartAt { get; set; }
    public DateTimeOffset EndAt { get; set; }

    /// <summary>Effective capacity including overrides and overbooking.</summary>
    public int TotalCapacity { get; set; }

    public int TotalBooked { get; set; }
    public bool IsClosed { get; set; }

    /// <summary>Set when capacity was reduced below what is already booked. Bookings are never auto-cancelled.</summary>
    public bool OverCapacity { get; set; }

    public DateTimeOffset GeneratedAt { get; set; }
    public List<SlotCategoryCapacity> Categories { get; set; } = [];

    public bool HasOverCapacity() =>
        TotalBooked > TotalCapacity || Categories.Any(c => c.Booked > c.QuotaCapacity);
}

public sealed class SlotCategoryCapacity
{
    public long SlotId { get; set; }
    public int CategoryId { get; set; }
    public int QuotaCapacity { get; set; }
    public int Booked { get; set; }
}
