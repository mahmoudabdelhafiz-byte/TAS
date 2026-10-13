using Tas.Application.Capacity;
using Tas.Domain.Capacity;
using static Tas.Application.Tests.TestData;

namespace Tas.Application.Tests;

public class SlotPlannerTests
{
    [Fact]
    public void Default_template_splits_window_into_hourly_slots_in_utc()
    {
        var config = Config([Default(Window(DayOfWeek.Tuesday, 7, 10, total: 40, quotas: [(Import, 25)]))]);

        var slots = SlotPlanner.Plan(config, Tuesday, Tuesday);

        Assert.Equal(3, slots.Count);
        Assert.Equal(Riyadh(2026, 10, 20, 7), slots[0].StartAt);
        Assert.Equal(TimeSpan.Zero, slots[0].StartAt.Offset); // stored as UTC
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 4, 0, 0, TimeSpan.Zero), slots[0].StartAt);
        Assert.Equal(Riyadh(2026, 10, 20, 8), slots[0].EndAt);
        Assert.All(slots, s => Assert.Equal(40, s.TotalCapacity));
        Assert.Equal(25, slots[0].CategoryQuotas[Import]);
    }

    [Fact]
    public void Category_without_explicit_quota_is_limited_only_by_total()
    {
        var config = Config([Default(Window(DayOfWeek.Tuesday, 7, 8, total: 40, quotas: [(Import, 25)]))]);

        var slot = Assert.Single(SlotPlanner.Plan(config, Tuesday, Tuesday));

        Assert.Equal(40, slot.CategoryQuotas[Export]);
        Assert.Equal(40, slot.CategoryQuotas[Empty]);
    }

    [Fact]
    public void Thirty_minute_slots_and_partial_tail_is_dropped()
    {
        // 07:00–08:45 → 07:00, 07:30, 08:00 (08:30–09:00 does not fit)
        var window = Window(DayOfWeek.Tuesday, 7, 8, total: 10);
        window.EndTime = new TimeOnly(8, 45);
        var config = Config([Default(window)], slotMinutes: 30);

        var slots = SlotPlanner.Plan(config, Tuesday, Tuesday);

        Assert.Equal(
            new[] { Riyadh(2026, 10, 20, 7), Riyadh(2026, 10, 20, 7, 30), Riyadh(2026, 10, 20, 8) },
            slots.Select(s => s.StartAt).ToArray());
    }

    [Fact]
    public void Window_ending_at_2359_runs_until_midnight()
    {
        var config = Config([Default(Window(DayOfWeek.Tuesday, 22, 24, total: 5))]);

        var slots = SlotPlanner.Plan(config, Tuesday, Tuesday);

        Assert.Equal(2, slots.Count);
        Assert.Equal(Riyadh(2026, 10, 21, 0), slots[1].EndAt);
    }

    [Fact]
    public void Days_without_a_window_get_no_slots()
    {
        var config = Config([Default(Window(DayOfWeek.Tuesday, 7, 8, total: 10))]);

        var slots = SlotPlanner.Plan(config, Tuesday.AddDays(1), Tuesday.AddDays(6));

        Assert.Empty(slots);
    }

    [Fact]
    public void Seasonal_template_replaces_default_inside_its_dates_only()
    {
        var config = Config([
            Default(
                Window(DayOfWeek.Tuesday, 7, 9, total: 40),
                Window(DayOfWeek.Wednesday, 7, 9, total: 40)),
            Seasonal(2, Tuesday, Tuesday, priority: 1, Window(DayOfWeek.Tuesday, 20, 21, total: 15)),
        ]);

        var slots = SlotPlanner.Plan(config, Tuesday, Tuesday.AddDays(1));

        var tuesday = slots.Where(s => s.StartAt < Riyadh(2026, 10, 21, 0)).ToList();
        var wednesday = slots.Where(s => s.StartAt >= Riyadh(2026, 10, 21, 0)).ToList();
        var ramadanSlot = Assert.Single(tuesday);
        Assert.Equal(Riyadh(2026, 10, 20, 20), ramadanSlot.StartAt);
        Assert.Equal(15, ramadanSlot.TotalCapacity);
        Assert.Equal(2, wednesday.Count);
        Assert.All(wednesday, s => Assert.Equal(40, s.TotalCapacity));
    }

    [Fact]
    public void Highest_priority_seasonal_template_wins()
    {
        var config = Config([
            Default(Window(DayOfWeek.Tuesday, 7, 8, total: 40)),
            Seasonal(2, Tuesday, Tuesday, priority: 1, Window(DayOfWeek.Tuesday, 7, 8, total: 20)),
            Seasonal(3, Tuesday, Tuesday, priority: 5, Window(DayOfWeek.Tuesday, 7, 8, total: 10)),
        ]);

        Assert.Equal(10, Assert.Single(SlotPlanner.Plan(config, Tuesday, Tuesday)).TotalCapacity);
    }

    [Fact]
    public void Inactive_templates_are_ignored()
    {
        var seasonal = Seasonal(2, Tuesday, Tuesday, priority: 1, Window(DayOfWeek.Tuesday, 7, 8, total: 5));
        seasonal.IsActive = false;
        var config = Config([Default(Window(DayOfWeek.Tuesday, 7, 8, total: 40)), seasonal]);

        Assert.Equal(40, Assert.Single(SlotPlanner.Plan(config, Tuesday, Tuesday)).TotalCapacity);
    }

    [Fact]
    public void Overrides_apply_only_to_overlapping_slots()
    {
        var config = Config(
            [Default(Window(DayOfWeek.Tuesday, 7, 10, total: 40))],
            [Override(Riyadh(2026, 10, 20, 8), Riyadh(2026, 10, 20, 9), OverrideAction.Set, 55)]);

        var slots = SlotPlanner.Plan(config, Tuesday, Tuesday);

        Assert.Equal(new[] { 40, 55, 40 }, slots.Select(s => s.TotalCapacity).ToArray());
    }

    [Fact]
    public void Total_overrides_apply_in_creation_order()
    {
        var start = Riyadh(2026, 10, 20, 7);
        var config = Config(
            [Default(Window(DayOfWeek.Tuesday, 7, 8, total: 40))],
            [
                Override(start, start.AddHours(1), OverrideAction.Adjust, -10, createdMinute: 2),
                Override(start, start.AddHours(1), OverrideAction.Set, 30, createdMinute: 1),
            ]);

        // SET 30 (earlier) then ADJUST -10 → 20
        Assert.Equal(20, Assert.Single(SlotPlanner.Plan(config, Tuesday, Tuesday)).TotalCapacity);
    }

    [Fact]
    public void Close_override_closes_slot_and_all_categories()
    {
        var start = Riyadh(2026, 10, 20, 7);
        var config = Config(
            [Default(Window(DayOfWeek.Tuesday, 7, 8, total: 40, quotas: [(Import, 25)]))],
            [Override(start, start.AddHours(1), OverrideAction.Close)]);

        var slot = Assert.Single(SlotPlanner.Plan(config, Tuesday, Tuesday));

        Assert.True(slot.IsClosed);
        Assert.Equal(0, slot.TotalCapacity);
        Assert.All(slot.CategoryQuotas.Values, q => Assert.Equal(0, q));
    }

    [Fact]
    public void Category_overrides_adjust_set_and_close_quotas()
    {
        var start = Riyadh(2026, 10, 20, 7);
        var config = Config(
            [Default(Window(DayOfWeek.Tuesday, 7, 8, total: 40, quotas: [(Import, 25), (Export, 20)]))],
            [
                Override(start, start.AddHours(1), OverrideAction.Adjust, 10, category: Export),
                Override(start, start.AddHours(1), OverrideAction.Close, category: Import),
                Override(start, start.AddHours(1), OverrideAction.Set, 5, category: Empty),
            ]);

        var slot = Assert.Single(SlotPlanner.Plan(config, Tuesday, Tuesday));

        Assert.False(slot.IsClosed);
        Assert.Equal(40, slot.TotalCapacity);
        Assert.Equal(0, slot.CategoryQuotas[Import]);
        Assert.Equal(30, slot.CategoryQuotas[Export]);
        Assert.Equal(5, slot.CategoryQuotas[Empty]);
    }

    [Fact]
    public void Category_quota_never_exceeds_slot_total()
    {
        var start = Riyadh(2026, 10, 20, 7);
        var config = Config(
            [Default(Window(DayOfWeek.Tuesday, 7, 8, total: 40, quotas: [(Import, 35)]))],
            [Override(start, start.AddHours(1), OverrideAction.Set, 20)]);

        var slot = Assert.Single(SlotPlanner.Plan(config, Tuesday, Tuesday));

        Assert.Equal(20, slot.TotalCapacity);
        Assert.Equal(20, slot.CategoryQuotas[Import]);
    }

    [Fact]
    public void Overbooking_is_applied_to_total_and_quotas_rounded_down()
    {
        var config = Config([Default(Window(DayOfWeek.Tuesday, 7, 8, total: 40, overbookPct: 10, quotas: [(Import, 25)]))]);

        var slot = Assert.Single(SlotPlanner.Plan(config, Tuesday, Tuesday));

        Assert.Equal(44, slot.TotalCapacity);          // 40 × 1.10
        Assert.Equal(27, slot.CategoryQuotas[Import]); // floor(25 × 1.10 = 27.5)
        Assert.Equal(44, slot.CategoryQuotas[Export]);
    }

    [Fact]
    public void Inactive_categories_get_no_quota_row()
    {
        var config = Config(
            [Default(Window(DayOfWeek.Tuesday, 7, 8, total: 40, quotas: [(Empty, 10)]))],
            categories: [Import, Export]);

        var slot = Assert.Single(SlotPlanner.Plan(config, Tuesday, Tuesday));

        Assert.Equal(new[] { Import, Export }, slot.CategoryQuotas.Keys.Order().ToArray());
    }

    [Fact]
    public void Overlapping_windows_do_not_create_duplicate_slots()
    {
        var config = Config([Default(
            Window(DayOfWeek.Tuesday, 7, 9, total: 40),
            Window(DayOfWeek.Tuesday, 8, 10, total: 10))]);

        var slots = SlotPlanner.Plan(config, Tuesday, Tuesday);

        Assert.Equal(new[] { 40, 40, 10 }, slots.Select(s => s.TotalCapacity).ToArray());
    }
}
