using Yardsyncs.Application.Capacity;
using Yardsyncs.Domain.Capacity;
using static Yardsyncs.Application.Tests.TestData;

namespace Yardsyncs.Application.Tests;

public class SlotReconcilerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Seven = Riyadh(2026, 10, 20, 7);
    private static readonly DateTimeOffset Eight = Riyadh(2026, 10, 20, 8);

    private static PlannedSlot Planned(DateTimeOffset start, int total, int importQuota, bool closed = false) =>
        new(start, start.AddHours(1), total, closed, new Dictionary<int, int> { [Import] = importQuota });

    private static Slot Stored(DateTimeOffset start, int total, int totalBooked, int importQuota, int importBooked) =>
        new()
        {
            SlotId = start.Hour,
            TerminalId = 1,
            StartAt = start,
            EndAt = start.AddHours(1),
            TotalCapacity = total,
            TotalBooked = totalBooked,
            Categories = [new SlotCategoryCapacity { SlotId = start.Hour, CategoryId = Import, QuotaCapacity = importQuota, Booked = importBooked }],
        };

    [Fact]
    public void New_slots_are_created_with_category_rows()
    {
        var outcome = SlotReconciler.Reconcile(1, [Planned(Seven, 40, 25)], [], Now);

        var slot = Assert.Single(outcome.ToAdd);
        Assert.Equal(40, slot.TotalCapacity);
        Assert.Equal(0, slot.TotalBooked);
        Assert.Equal(25, Assert.Single(slot.Categories).QuotaCapacity);
        Assert.Equal(0, outcome.Updated);
    }

    [Fact]
    public void Existing_slots_keep_bookings_when_capacity_changes()
    {
        var stored = Stored(Seven, total: 40, totalBooked: 12, importQuota: 25, importBooked: 10);

        var outcome = SlotReconciler.Reconcile(1, [Planned(Seven, 30, 20)], [stored], Now);

        Assert.Empty(outcome.ToAdd);
        Assert.Equal(1, outcome.Updated);
        Assert.Equal(30, stored.TotalCapacity);
        Assert.Equal(12, stored.TotalBooked);
        Assert.Equal(20, stored.Categories[0].QuotaCapacity);
        Assert.Equal(10, stored.Categories[0].Booked);
        Assert.False(stored.OverCapacity);
    }

    [Fact]
    public void Reducing_below_bookings_flags_over_capacity_instead_of_cancelling()
    {
        var stored = Stored(Seven, total: 40, totalBooked: 35, importQuota: 25, importBooked: 25);

        var outcome = SlotReconciler.Reconcile(1, [Planned(Seven, 20, 10)], [stored], Now);

        Assert.True(stored.OverCapacity);
        Assert.Equal(35, stored.TotalBooked);
        Assert.Equal(1, outcome.OverCapacity);
    }

    [Fact]
    public void Slots_no_longer_planned_are_closed_not_deleted()
    {
        var stillPlanned = Stored(Seven, 40, 0, 25, 0);
        var removed = Stored(Eight, 40, 3, 25, 3);

        var outcome = SlotReconciler.Reconcile(1, [Planned(Seven, 40, 25)], [stillPlanned, removed], Now);

        Assert.Equal(1, outcome.Orphaned);
        Assert.True(removed.IsClosed);
        Assert.Equal(0, removed.TotalCapacity);
        Assert.Equal(3, removed.TotalBooked);
        Assert.True(removed.OverCapacity);
        Assert.False(stillPlanned.IsClosed);
    }

    [Fact]
    public void Newly_active_category_gets_a_row_and_dropped_category_gets_zero()
    {
        var stored = Stored(Seven, 40, 0, 25, 0);
        var planned = new PlannedSlot(Seven, Seven.AddHours(1), 40, false, new Dictionary<int, int> { [Export] = 15 });

        SlotReconciler.Reconcile(1, [planned], [stored], Now);

        Assert.Equal(0, stored.Categories.Single(c => c.CategoryId == Import).QuotaCapacity);
        Assert.Equal(15, stored.Categories.Single(c => c.CategoryId == Export).QuotaCapacity);
    }
}
