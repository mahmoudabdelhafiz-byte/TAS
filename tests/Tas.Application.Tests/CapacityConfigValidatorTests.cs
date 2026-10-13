using Tas.Application.Capacity;
using Tas.Domain.Capacity;

namespace Tas.Application.Tests;

public class CapacityConfigValidatorTests
{
    private static readonly Dictionary<string, int> Categories = new() { ["IMP_PICKUP"] = 1, ["EXP_DROP"] = 2 };
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 9, 0, 0, TimeSpan.Zero);

    private static TemplateWindowInput Window(DayOfWeek day, string start, string end, int total = 30, Dictionary<string, int>? quotas = null) =>
        new(day, start, end, total, 0, quotas);

    private static TemplateInput Default(params TemplateWindowInput[] windows) => new("Default", true, null, null, 0, windows);

    // ------------------------------------------------------------ templates

    [Fact]
    public void Valid_template_is_converted_with_quotas_and_midnight_end()
    {
        var (template, errors) = CapacityConfigValidator.ValidateTemplate(
            Default(
                Window(DayOfWeek.Sunday, "06:00", "14:00", quotas: new() { ["imp_pickup"] = 15 }),
                Window(DayOfWeek.Sunday, "14:00", "24:00", total: 20)),
            Categories);

        Assert.Empty(errors);
        Assert.Equal(2, template!.Slots.Count);
        Assert.Equal(1, template.Slots[0].Quotas.Single().CategoryId);
        Assert.Equal(new TimeOnly(23, 59), template.Slots[1].EndTime);

        var view = CapacityConfigValidator.ToView(template, new Dictionary<int, string> { [1] = "IMP_PICKUP" });
        Assert.Equal("24:00", view.Windows[1].End);
        Assert.Equal(15, view.Windows[0].Quotas["IMP_PICKUP"]);
    }

    [Fact]
    public void Overlapping_windows_on_the_same_day_are_refused()
    {
        var (template, errors) = CapacityConfigValidator.ValidateTemplate(
            Default(
                Window(DayOfWeek.Monday, "06:00", "12:00"),
                Window(DayOfWeek.Monday, "11:00", "14:00"),
                Window(DayOfWeek.Tuesday, "11:00", "14:00")),
            Categories);

        Assert.Null(template);
        Assert.Contains(errors, e => e.Contains("Monday") && e.Contains("overlap"));
        Assert.DoesNotContain(errors, e => e.Contains("Tuesday"));
    }

    [Fact]
    public void Each_problem_in_a_window_is_reported()
    {
        var (_, errors) = CapacityConfigValidator.ValidateTemplate(
            Default(
                Window(DayOfWeek.Monday, "7am", "12:00"),
                Window(DayOfWeek.Monday, "14:00", "13:00"),
                Window(DayOfWeek.Monday, "15:00", "16:00", total: -1),
                Window(DayOfWeek.Monday, "16:00", "17:00", quotas: new() { ["NOPE"] = 3 }),
                new TemplateWindowInput(DayOfWeek.Monday, "18:00", "19:00", 10, OverbookPct: 80)),
            Categories);

        Assert.Contains(errors, e => e.Contains("HH:mm"));
        Assert.Contains(errors, e => e.Contains("end must be after start"));
        Assert.Contains(errors, e => e.Contains("total capacity"));
        Assert.Contains(errors, e => e.Contains("unknown category 'NOPE'"));
        Assert.Contains(errors, e => e.Contains("overbooking"));
    }

    [Fact]
    public void Default_and_seasonal_date_rules()
    {
        var window = Window(DayOfWeek.Sunday, "06:00", "14:00");

        Assert.NotEmpty(CapacityConfigValidator.ValidateTemplate(
            new TemplateInput("x", true, new DateOnly(2027, 2, 1), null, 0, [window]), Categories).Errors);
        Assert.NotEmpty(CapacityConfigValidator.ValidateTemplate(
            new TemplateInput("Ramadan", false, null, null, 0, [window]), Categories).Errors);
        Assert.NotEmpty(CapacityConfigValidator.ValidateTemplate(
            new TemplateInput("Ramadan", false, new DateOnly(2027, 3, 1), new DateOnly(2027, 2, 1), 0, [window]), Categories).Errors);
        Assert.Empty(CapacityConfigValidator.ValidateTemplate(
            new TemplateInput("Ramadan", false, new DateOnly(2027, 2, 8), new DateOnly(2027, 3, 9), 1, [window]), Categories).Errors);
        Assert.NotEmpty(CapacityConfigValidator.ValidateTemplate(
            new TemplateInput("Empty", true, null, null, 0, []), Categories).Errors);
    }

    // ------------------------------------------------------------ overrides

    private static OverrideInput Override(OverrideAction action, int? value, string? category = null, string reason = "Crane 3 maintenance",
        int startHours = 24, int lengthHours = 2) =>
        new(Now.AddHours(startHours), Now.AddHours(startHours + lengthHours), category, action, value, reason);

    [Fact]
    public void Valid_overrides_are_accepted_and_stored_in_utc()
    {
        var local = new OverrideInput(
            new DateTimeOffset(2026, 10, 20, 10, 0, 0, TimeSpan.FromHours(3)),
            new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.FromHours(3)),
            "exp_drop", OverrideAction.Adjust, -5, " Vessel delayed ");

        var (item, errors) = CapacityConfigValidator.ValidateOverride(local, Categories, Now);

        Assert.Empty(errors);
        Assert.Equal(TimeSpan.Zero, item!.StartAt.Offset);
        Assert.Equal(7, item.StartAt.Hour);
        Assert.Equal(2, item.CategoryId);
        Assert.Equal("Vessel delayed", item.Reason);
        Assert.Null(CapacityConfigValidator.ValidateOverride(Override(OverrideAction.Close, null), Categories, Now).Override!.Value);
    }

    [Theory]
    [InlineData(OverrideAction.Set, null)]
    [InlineData(OverrideAction.Set, -1)]
    [InlineData(OverrideAction.Adjust, 0)]
    [InlineData(OverrideAction.Adjust, null)]
    [InlineData(OverrideAction.Close, 5)]
    public void Action_value_combinations_are_checked(OverrideAction action, int? value) =>
        Assert.NotEmpty(CapacityConfigValidator.ValidateOverride(Override(action, value), Categories, Now).Errors);

    [Fact]
    public void Override_needs_reason_future_end_known_category_and_bounded_length()
    {
        Assert.NotEmpty(CapacityConfigValidator.ValidateOverride(Override(OverrideAction.Close, null, reason: " "), Categories, Now).Errors);
        Assert.NotEmpty(CapacityConfigValidator.ValidateOverride(Override(OverrideAction.Close, null, startHours: -5, lengthHours: 2), Categories, Now).Errors);
        Assert.NotEmpty(CapacityConfigValidator.ValidateOverride(Override(OverrideAction.Close, null, category: "NOPE"), Categories, Now).Errors);
        Assert.NotEmpty(CapacityConfigValidator.ValidateOverride(Override(OverrideAction.Close, null, lengthHours: 24 * 100), Categories, Now).Errors);
        Assert.NotEmpty(CapacityConfigValidator.ValidateOverride(Override(OverrideAction.Close, null, lengthHours: 0), Categories, Now).Errors);
    }

    // ------------------------------------------------------------ reservations, release rules, settings

    [Fact]
    public void Reservation_rules()
    {
        var start = Now.AddDays(1);
        Assert.Empty(CapacityConfigValidator.ValidateReservation(new ReservationInput(null, null, start, start.AddDays(7), 2, 2), Categories).Errors);
        Assert.NotEmpty(CapacityConfigValidator.ValidateReservation(new ReservationInput(null, null, start, start.AddDays(7), 0, 2), Categories).Errors);
        Assert.NotEmpty(CapacityConfigValidator.ValidateReservation(new ReservationInput(null, null, start, start, 2, 2), Categories).Errors);
        Assert.NotEmpty(CapacityConfigValidator.ValidateReservation(new ReservationInput(5, "NOPE", start, start.AddDays(1), 2, 2), Categories).Errors);
    }

    [Fact]
    public void Release_rule_rules()
    {
        var (rules, errors) = CapacityConfigValidator.ValidateReleaseRules([new ReleaseRuleInput("imp_pickup", 12)], Categories);
        Assert.Empty(errors);
        Assert.Equal(1, rules.Single().CategoryId);

        Assert.NotEmpty(CapacityConfigValidator.ValidateReleaseRules(
            [new ReleaseRuleInput("IMP_PICKUP", 12), new ReleaseRuleInput("IMP_PICKUP", 6)], Categories).Errors);
        Assert.NotEmpty(CapacityConfigValidator.ValidateReleaseRules([new ReleaseRuleInput("IMP_PICKUP", 500)], Categories).Errors);
        Assert.Empty(CapacityConfigValidator.ValidateReleaseRules([], Categories).Errors);
    }

    [Fact]
    public void Setting_rules()
    {
        Assert.Empty(CapacityConfigValidator.ValidateSettings(new Dictionary<string, int> { ["early_grace_min"] = 45 }));
        Assert.NotEmpty(CapacityConfigValidator.ValidateSettings(new Dictionary<string, int> { ["early_grace_min"] = 999 }));
        Assert.NotEmpty(CapacityConfigValidator.ValidateSettings(new Dictionary<string, int> { ["coffee_breaks"] = 2 }));
    }
}
