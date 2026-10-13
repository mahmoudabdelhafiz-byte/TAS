using System.Text.RegularExpressions;
using Tas.Application.Booking;
using Tas.Domain;
using Tas.Domain.Booking;

namespace Tas.Application.Tests;

public class ContainerNumberTests
{
    [Theory]
    [InlineData("CSQU3054383")] // ISO 6346 reference example
    [InlineData("MSCU1234566")]
    [InlineData("MAEU4001110")] // check digit 10 → 0
    public void Valid_numbers_pass(string number) => Assert.True(ContainerNumber.IsValid(number));

    [Theory]
    [InlineData("MSCU1234567")] // wrong check digit
    [InlineData("MSC1234566")]  // too short
    [InlineData("MSCX1234566")] // category must be U, J or Z
    public void Invalid_numbers_fail(string number) => Assert.False(ContainerNumber.IsValid(number));

    [Fact]
    public void Normalize_strips_spaces_and_dashes_and_upper_cases() =>
        Assert.Equal("CSQU3054383", ContainerNumber.Normalize(" cs qu 305438-3 "));
}

public class BookingRequestValidatorTests
{
    private static readonly Dictionary<string, TransactionCategory> Categories = new()
    {
        ["IMP_PICKUP"] = new() { CategoryId = 1, Code = "IMP_PICKUP", Mode = CategoryMode.Container },
        ["EXP_DROP"] = new() { CategoryId = 2, Code = "EXP_DROP", Mode = CategoryMode.Container },
        ["MTY_PICKUP"] = new() { CategoryId = 3, Code = "MTY_PICKUP", Mode = CategoryMode.Container },
        ["BB_DELIVERY"] = new() { CategoryId = 4, Code = "BB_DELIVERY", Mode = CategoryMode.Cargo },
    };

    private static BookingRequest Request(params BookingItemRequest[] items) => new(1, 1, 1, null, null, items);

    [Fact]
    public void Dual_transaction_is_valid_and_normalized()
    {
        var (items, errors) = BookingRequestValidator.Validate(
            Request(
                new BookingItemRequest("exp_drop", ContainerNo: "hlxu 870001-8", BookingRef: " bkg1001 "),
                new BookingItemRequest("IMP_PICKUP", ContainerNo: "MSCU1234566")),
            Categories);

        Assert.Empty(errors);
        Assert.Equal(2, items.Count);
        Assert.Equal("HLXU8700018", items[0].ContainerNo);
        Assert.Equal("BKG1001", items[0].BookingRef);
        Assert.Equal(2, items[0].Category.CategoryId);
    }

    [Fact]
    public void Empty_pickup_by_booking_reference_only_is_valid()
    {
        var (_, errors) = BookingRequestValidator.Validate(Request(new BookingItemRequest("MTY_PICKUP", BookingRef: "REL-55")), Categories);
        Assert.Empty(errors);
    }

    [Fact]
    public void Cargo_item_needs_cargo_reference_and_no_container()
    {
        var (_, errors) = BookingRequestValidator.Validate(
            Request(new BookingItemRequest("BB_DELIVERY", ContainerNo: "MSCU1234566")), Categories);

        Assert.Contains(errors, e => e.Contains("needs a cargo reference"));
        Assert.Contains(errors, e => e.Contains("remove the container number"));
    }

    [Fact]
    public void Problems_are_all_reported()
    {
        var (items, errors) = BookingRequestValidator.Validate(
            Request(
                new BookingItemRequest("NOPE", ContainerNo: "MSCU1234566"),
                new BookingItemRequest("IMP_PICKUP"),
                new BookingItemRequest("IMP_PICKUP", ContainerNo: "XX12"),
                new BookingItemRequest("IMP_PICKUP", ContainerNo: "MSCU1234566"),
                new BookingItemRequest("IMP_PICKUP", ContainerNo: "MSCU1234566")),
            Categories);

        Assert.Empty(items);
        Assert.Contains(errors, e => e.Contains("at most 4 items"));
        Assert.Contains(errors, e => e.Contains("unknown category 'NOPE'"));
        Assert.Contains(errors, e => e.Contains("needs a container number"));
        Assert.Contains(errors, e => e.Contains("not a valid container number"));
        Assert.Contains(errors, e => e.Contains("appears twice"));
    }

    [Fact]
    public void No_items_is_invalid()
    {
        var (_, errors) = BookingRequestValidator.Validate(Request(), Categories);
        Assert.Single(errors);
    }
}

public class TrafficLightCalculatorTests
{
    private static readonly TimeZoneInfo Riyadh = TimeZoneInfo.FindSystemTimeZoneById("Asia/Riyadh");
    private static readonly DateTimeOffset Start = new(2026, 10, 20, 10, 0, 0, TimeSpan.FromHours(3));
    private static readonly DateTimeOffset End = Start.AddHours(1);
    private static readonly TerminalSettings Settings = TerminalSettings.Defaults; // 30 min early / late grace

    private static ItemCheckState Item(ValidationStatus status, string? problem = null) => new("Item 1", status, problem);

    private static (TrafficLight Light, string Reason) At(DateTimeOffset now, params ItemCheckState[] items) =>
        TrafficLightCalculator.Calculate(AppointmentStatus.Confirmed, items, Start, End, now, Settings, Riyadh);

    [Fact]
    public void Failed_check_is_red_with_its_reason()
    {
        var (light, reason) = At(Start.AddDays(-1), Item(ValidationStatus.Fail, "Customs hold on MSCU7654329"));
        Assert.Equal(TrafficLight.Red, light);
        Assert.Equal("Customs hold on MSCU7654329", reason);
    }

    [Fact]
    public void Inside_the_window_with_grace_is_green()
    {
        Assert.Equal(TrafficLight.Green, At(Start.AddMinutes(-30), Item(ValidationStatus.Pass)).Light);
        Assert.Equal(TrafficLight.Green, At(End.AddMinutes(30), Item(ValidationStatus.Pass)).Light);
    }

    [Fact]
    public void Too_early_is_grey_with_local_time()
    {
        var (light, reason) = At(Start.AddHours(-3), Item(ValidationStatus.Pass));
        Assert.Equal(TrafficLight.Grey, light);
        Assert.Contains("10:00", reason);
        Assert.Contains("09:30", reason);
    }

    [Fact]
    public void After_the_window_is_red()
    {
        var (light, reason) = At(End.AddMinutes(31), Item(ValidationStatus.Pass));
        Assert.Equal(TrafficLight.Red, light);
        Assert.Contains("reschedule", reason);
    }

    [Fact]
    public void Warning_is_amber()
    {
        Assert.Equal(TrafficLight.Amber, At(Start, Item(ValidationStatus.Pass), Item(ValidationStatus.Warn, "Storage starts today")).Light);
    }

    [Fact]
    public void Final_statuses_ignore_checks()
    {
        var (light, _) = TrafficLightCalculator.Calculate(
            AppointmentStatus.Cancelled, [Item(ValidationStatus.Fail, "x")], Start, End, Start, Settings, Riyadh);
        Assert.Equal(TrafficLight.Grey, light);
    }

    [Fact]
    public void Summarize_takes_the_worst_result()
    {
        Assert.Equal(ValidationStatus.Fail, TrafficLightCalculator.Summarize([CheckResult.Pass, CheckResult.Fail, CheckResult.Warn]));
        Assert.Equal(ValidationStatus.Warn, TrafficLightCalculator.Summarize([CheckResult.Pass, CheckResult.Warn]));
        Assert.Equal(ValidationStatus.Pass, TrafficLightCalculator.Summarize([CheckResult.Pass]));
        Assert.Equal(ValidationStatus.Pending, TrafficLightCalculator.Summarize([]));
    }
}

public class AppointmentNumberAndSettingsTests
{
    [Fact]
    public void Appointment_numbers_are_readable_and_fit_the_column()
    {
        var number = AppointmentNumbers.New("JED-TERMINAL-1", new DateOnly(2026, 10, 20));

        Assert.Matches(new Regex("^JEDTER-261020-[2-9A-HJKMNP-Z]{5}$"), number);
        Assert.True(number.Length <= 20);
        Assert.Equal(48, AppointmentNumbers.NewQrToken().Length);
    }

    [Fact]
    public void Settings_fall_back_to_defaults_for_missing_or_bad_values()
    {
        var settings = TerminalSettings.From(
        [
            new TerminalSetting { SettingKey = "early_grace_min", SettingValue = "15" },
            new TerminalSetting { SettingKey = "late_grace_min", SettingValue = "oops" },
        ]);

        Assert.Equal(15, settings.EarlyGraceMinutes);
        Assert.Equal(TerminalSettings.Defaults.LateGraceMinutes, settings.LateGraceMinutes);
        Assert.Equal(TerminalSettings.Defaults.CancelCutoffMinutes, settings.CancelCutoffMinutes);
    }
}
