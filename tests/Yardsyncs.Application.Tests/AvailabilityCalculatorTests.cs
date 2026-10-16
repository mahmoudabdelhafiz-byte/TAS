using Yardsyncs.Application.Capacity;
using Yardsyncs.Domain.Capacity;
using static Yardsyncs.Application.Tests.TestData;

namespace Yardsyncs.Application.Tests;

public class AvailabilityCalculatorTests
{
    private static readonly DateTimeOffset SlotStart = Riyadh(2026, 10, 20, 10);
    private static readonly DateTimeOffset DayBefore = SlotStart.AddDays(-1);

    private static SlotCounters Counters(
        int total = 40, int totalBooked = 0, int quota = 25, int categoryBooked = 0, bool closed = false) =>
        new(SlotStart, SlotStart.AddHours(1), total, totalBooked, closed, quota, categoryBooked);

    [Fact]
    public void Limited_by_category_quota()
    {
        Assert.Equal(5, AvailabilityCalculator.Available(Counters(quota: 25, categoryBooked: 20), DayBefore, 0, null));
    }

    [Fact]
    public void Limited_by_slot_total_when_other_categories_used_it()
    {
        Assert.Equal(3, AvailabilityCalculator.Available(Counters(totalBooked: 37, quota: 25, categoryBooked: 2), DayBefore, 0, null));
    }

    [Fact]
    public void Reserved_units_are_not_available()
    {
        Assert.Equal(2, AvailabilityCalculator.Available(Counters(totalBooked: 30, quota: 40), DayBefore, reservedUnits: 8, null));
    }

    [Fact]
    public void Released_quota_opens_the_whole_free_total()
    {
        var counters = Counters(totalBooked: 25, quota: 25, categoryBooked: 25);
        var fourHoursBefore = SlotStart.AddHours(-4);

        Assert.Equal(0, AvailabilityCalculator.Available(counters, SlotStart.AddHours(-13), 0, releaseHoursBefore: 12));
        Assert.Equal(15, AvailabilityCalculator.Available(counters, fourHoursBefore, 0, releaseHoursBefore: 12));
    }

    [Fact]
    public void Closed_slot_has_nothing_available()
    {
        Assert.Equal(0, AvailabilityCalculator.Available(Counters(closed: true), DayBefore, 0, null));
    }

    [Fact]
    public void Slot_that_has_ended_has_nothing_available()
    {
        Assert.Equal(0, AvailabilityCalculator.Available(Counters(), SlotStart.AddHours(1), 0, null));
    }

    [Fact]
    public void Never_negative_when_over_capacity()
    {
        Assert.Equal(0, AvailabilityCalculator.Available(Counters(total: 20, totalBooked: 30), DayBefore, 0, null));
    }

    [Fact]
    public void Reservations_held_for_others_count_until_released()
    {
        var reservations = new[]
        {
            // Priority customer 7: 5 units, released 6 h before the slot.
            new CapacityReservation { CompanyId = 7, StartAt = SlotStart.AddDays(-30), EndAt = SlotStart.AddDays(30), UnitsPerSlot = 5, ReleaseHoursBefore = 6 },
            // Walk-in reserve: 2 units, never released.
            new CapacityReservation { CompanyId = null, StartAt = SlotStart.AddDays(-30), EndAt = SlotStart.AddDays(30), UnitsPerSlot = 2 },
            // Inactive and out-of-range reservations are ignored.
            new CapacityReservation { CompanyId = 8, StartAt = SlotStart.AddDays(-30), EndAt = SlotStart.AddDays(30), UnitsPerSlot = 9, IsActive = false },
            new CapacityReservation { CompanyId = 8, StartAt = SlotStart.AddHours(1), EndAt = SlotStart.AddDays(30), UnitsPerSlot = 9 },
        };

        Assert.Equal(7, AvailabilityCalculator.ReservedUnits(reservations, SlotStart, DayBefore, bookingCompanyId: 99));
        Assert.Equal(2, AvailabilityCalculator.ReservedUnits(reservations, SlotStart, DayBefore, bookingCompanyId: 7));
        Assert.Equal(2, AvailabilityCalculator.ReservedUnits(reservations, SlotStart, SlotStart.AddHours(-3), bookingCompanyId: 99));
    }
}
