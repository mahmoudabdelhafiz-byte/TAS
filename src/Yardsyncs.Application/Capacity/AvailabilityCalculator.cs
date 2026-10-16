using Yardsyncs.Domain.Capacity;

namespace Yardsyncs.Application.Capacity;

/// <summary>Current counters of one slot as seen by a booking for one category.</summary>
public sealed record SlotCounters(
    DateTimeOffset StartAt,
    DateTimeOffset EndAt,
    int TotalCapacity,
    int TotalBooked,
    bool IsClosed,
    int CategoryQuota,
    int CategoryBooked);

/// <summary>How many more trucks of a category can still book a slot right now.</summary>
public static class AvailabilityCalculator
{
    /// <param name="reservedUnits">Units held back for others (see <see cref="ReservedUnits"/>).</param>
    /// <param name="releaseHoursBefore">Quota release rule for the category, if any.</param>
    public static int Available(SlotCounters slot, DateTimeOffset now, int reservedUnits, int? releaseHoursBefore)
    {
        if (slot.IsClosed || now >= slot.EndAt)
            return 0;

        var freeTotal = slot.TotalCapacity - slot.TotalBooked - reservedUnits;
        var free = IsQuotaReleased(slot.StartAt, now, releaseHoursBefore)
            ? freeTotal
            : Math.Min(slot.CategoryQuota - slot.CategoryBooked, freeTotal);

        return Math.Max(0, free);
    }

    /// <summary>True once the category's unused quota has opened to the shared pool.</summary>
    public static bool IsQuotaReleased(DateTimeOffset slotStart, DateTimeOffset now, int? releaseHoursBefore) =>
        releaseHoursBefore is { } hours && now >= slotStart.AddHours(-hours);

    /// <summary>
    /// Units still held back from <paramref name="bookingCompanyId"/> in the slot starting at
    /// <paramref name="slotStart"/>: active reservations covering the slot that belong to someone else
    /// (or to walk-ins) and have not yet been released.
    /// </summary>
    /// <param name="walkIn">A truck booked at the gate may use the walk-in reserve (reservations without a company).</param>
    public static int ReservedUnits(
        IEnumerable<CapacityReservation> reservations, DateTimeOffset slotStart, DateTimeOffset now, int? bookingCompanyId, bool walkIn = false) =>
        reservations
            .Where(r => r.IsActive
                        && r.StartAt <= slotStart && slotStart < r.EndAt
                        && !(bookingCompanyId is not null && r.CompanyId == bookingCompanyId)
                        && !(walkIn && r.CompanyId is null)
                        && now < slotStart.AddHours(-r.ReleaseHoursBefore))
            .Sum(r => r.UnitsPerSlot);
}
