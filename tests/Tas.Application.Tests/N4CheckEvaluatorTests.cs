using Tas.Application.Tos;
using Tas.Application.Tos.N4;
using Tas.Domain;
using Tas.Domain.Booking;

namespace Tas.Application.Tests;

public class N4HoldClassifierTests
{
    [Theory]
    [InlineData("CUSTOMS HOLD", CheckType.CustomsRelease)]
    [InlineData("FASAH_PENDING", CheckType.CustomsRelease)]
    [InlineData("LINE_HOLD", CheckType.LineRelease)]
    [InlineData("UNPAID", CheckType.Payment)]
    [InlineData("storage due", CheckType.Payment)]
    [InlineData("INSPECTION", CheckType.TerminalHold)]
    public void Holds_are_classified_by_keyword(string holdId, CheckType expected) =>
        Assert.Equal(expected, new N4HoldClassifier().Classify(holdId));

    [Fact]
    public void Explicit_mapping_wins_and_is_case_insensitive()
    {
        var classifier = new N4HoldClassifier(new Dictionary<string, CheckType> { ["H12"] = CheckType.CustomsRelease, ["LINE_X"] = CheckType.TerminalHold });

        Assert.Equal(CheckType.CustomsRelease, classifier.Classify(" h12 "));
        Assert.Equal(CheckType.TerminalHold, classifier.Classify("line_x"));
    }
}

public class N4CheckEvaluatorTests
{
    private static readonly TimeZoneInfo Riyadh = TimeZoneInfo.FindSystemTimeZoneById("Asia/Riyadh");
    private static readonly DateTimeOffset Slot = new(2026, 10, 20, 10, 0, 0, TimeSpan.FromHours(3));
    private static readonly N4CheckEvaluator Evaluator = new(new N4HoldClassifier(), Riyadh);

    private static TosItemQuery Item(string category, string? container = null, string? booking = null, string? cargo = null, int index = 0) =>
        new(index, category, cargo is null ? CategoryMode.Container : CategoryMode.Cargo, container, booking, null, cargo, Slot);

    private static N4LookupResult N4(N4Unit[]? units = null, N4Order[]? orders = null, N4BillOfLading[]? bls = null) =>
        new(units ?? Array.Empty<N4Unit>(), orders ?? Array.Empty<N4Order>(), bls ?? Array.Empty<N4BillOfLading>());

    private static N4Unit Import(string state = "S40_YARD", params string[] holds) =>
        new("MSCU1234566", true, "100", "IMPRT", "FCL", state, "MSC", 24000, holds);

    private static IReadOnlyList<TosCheck> Run(TosItemQuery item, N4LookupResult n4) => Evaluator.Evaluate([item], n4);

    private static TosCheck Check(IReadOnlyList<TosCheck> checks, CheckType type) => Assert.Single(checks, c => c.Type == type);

    [Fact]
    public void Clear_import_in_the_yard_passes_every_check_with_the_gkey()
    {
        var checks = Run(Item("IMP_PICKUP", "MSCU1234566"), N4([Import()]));

        Assert.All(checks, c => Assert.Equal(CheckResult.Pass, c.Result));
        Assert.Equal(
            new[] { CheckType.Availability, CheckType.CustomsRelease, CheckType.LineRelease, CheckType.TerminalHold, CheckType.Payment },
            checks.Select(c => c.Type).ToArray());
        Assert.All(checks, c => Assert.Equal("100", c.TosRef));
    }

    [Theory]
    [InlineData("S20_INBOUND", "not in the yard yet")]
    [InlineData("S10_ADVISED", "not in the yard yet")]
    [InlineData("S70_DEPARTED", "already left")]
    [InlineData("S50_ECOUT", "already left")]
    public void Import_not_in_the_yard_fails_availability(string state, string message)
    {
        var availability = Check(Run(Item("IMP_PICKUP", "MSCU1234566"), N4([Import(state)])), CheckType.Availability);

        Assert.Equal(CheckResult.Fail, availability.Result);
        Assert.Contains(message, availability.Message);
    }

    [Fact]
    public void Unknown_or_non_import_container_fails()
    {
        Assert.Contains("not known in N4", Check(Run(Item("IMP_PICKUP", "MSCU1234566"), N4([new N4Unit("MSCU1234566", false)])), CheckType.Availability).Message);
        Assert.Contains("not known in N4", Check(Run(Item("IMP_PICKUP", "MSCU1234566"), N4()), CheckType.Availability).Message);

        var export = Import() with { Category = "EXPRT" };
        Assert.Contains("not an import container", Check(Run(Item("IMP_PICKUP", "MSCU1234566"), N4([export])), CheckType.Availability).Message);
    }

    [Fact]
    public void Each_active_hold_fails_its_own_check()
    {
        var checks = Run(Item("IMP_PICKUP", "MSCU1234566"), N4([Import("S40_YARD", "CUSTOMS HOLD", "UNPAID")]));

        Assert.Equal("Customs hold on MSCU1234566: CUSTOMS HOLD", Check(checks, CheckType.CustomsRelease).Message);
        Assert.Equal(CheckResult.Fail, Check(checks, CheckType.Payment).Result);
        Assert.Equal(CheckResult.Pass, Check(checks, CheckType.LineRelease).Result);
        Assert.Equal(CheckResult.Pass, Check(checks, CheckType.Availability).Result);
    }

    [Fact]
    public void Export_booking_cut_off_vgm_and_duplicate_rules()
    {
        var booking = new N4Order("BKG1001", true, "777", "BOOK", "HLC", 3, Slot.AddHours(-2));
        var preAdvised = new N4Unit("HLXU8700018", true, "200", "EXPRT", "FCL", "S10_ADVISED", "HLC", null, Array.Empty<string>());

        var checks = Run(Item("EXP_DROP", "HLXU8700018", booking: "bkg1001"), N4([preAdvised], [booking]));

        Assert.Equal(CheckResult.Pass, Check(checks, CheckType.BookingValid).Result);
        var cutoff = Check(checks, CheckType.Cutoff);
        Assert.Equal(CheckResult.Fail, cutoff.Result);
        Assert.Contains("20 Oct 08:00", cutoff.Message); // local Riyadh time
        Assert.Equal(CheckResult.Warn, Check(checks, CheckType.Vgm).Result);
        Assert.DoesNotContain(checks, c => c.Type == CheckType.Availability);

        var inYard = preAdvised with { TransitState = "S40_YARD", VgmKg = 22000 };
        var later = booking with { CargoCutoff = Slot.AddDays(1) };
        checks = Run(Item("EXP_DROP", "HLXU8700018", booking: "BKG1001"), N4([inYard], [later]));
        Assert.Contains("already in the terminal", Check(checks, CheckType.Availability).Message);
        Assert.Equal(CheckResult.Pass, Check(checks, CheckType.Cutoff).Result);
        Assert.Equal(CheckResult.Pass, Check(checks, CheckType.Vgm).Result);
    }

    [Fact]
    public void Export_without_a_usable_booking_fails()
    {
        Assert.Contains("needs a booking", Check(Run(Item("EXP_DROP", "HLXU8700018"), N4()), CheckType.BookingValid).Message);
        Assert.Contains("not known in N4", Check(Run(Item("EXP_DROP", "HLXU8700018", booking: "BKG9"), N4()), CheckType.BookingValid).Message);

        var full = new N4Order("BKG1001", true, "777", "BOOK", "HLC", 0, null);
        Assert.Contains("no containers left", Check(Run(Item("EXP_DROP", "HLXU8700018", booking: "BKG1001"), N4(orders: [full])), CheckType.BookingValid).Message);
    }

    [Fact]
    public void Empty_pickup_and_return_rules()
    {
        var release = new N4Order("REL-55", true, "9", "EDO", "MSC", 2, null);
        Assert.Equal(CheckResult.Pass, Check(Run(Item("MTY_PICKUP", booking: "REL-55"), N4(orders: [release])), CheckType.BookingValid).Result);
        Assert.Equal(CheckResult.Fail, Check(Run(Item("MTY_PICKUP", booking: "REL-55"), N4(orders: [release with { QuantityRemaining = 0 }])), CheckType.BookingValid).Result);

        var empty = new N4Unit("MAEU4001110", true, "300", "STRGE", "MTY", "S40_YARD", "MAERSK", null, Array.Empty<string>());
        Assert.Contains("already in the terminal", Check(Run(Item("MTY_RETURN", "MAEU4001110"), N4([empty])), CheckType.Availability).Message);
        Assert.Equal(CheckResult.Pass, Check(Run(Item("MTY_RETURN", "MAEU4001110"), N4()), CheckType.Availability).Result);
    }

    [Fact]
    public void Breakbulk_bl_rules()
    {
        Assert.Contains("not known", Check(Run(Item("BB_DELIVERY", cargo: "BL-1"), N4()), CheckType.CargoRelease).Message);

        var notReleased = new N4BillOfLading("BL-1", true, "400", false, new[] { "CUSTOMS_INBOND" });
        var checks = Run(Item("BB_DELIVERY", cargo: "BL-1"), N4(bls: [notReleased]));
        Assert.Contains("not released", Check(checks, CheckType.CargoRelease).Message);
        Assert.Equal(CheckResult.Fail, Check(checks, CheckType.CustomsRelease).Result);
        Assert.Equal(CheckResult.Pass, Check(checks, CheckType.Payment).Result);

        Assert.All(Run(Item("BB_DELIVERY", cargo: "BL-1"), N4(bls: [new N4BillOfLading("BL-1", true, "400", true, Array.Empty<string>())])),
            c => Assert.Equal(CheckResult.Pass, c.Result));
    }

    [Fact]
    public void Request_asks_only_for_what_the_items_need()
    {
        var request = N4CheckEvaluator.RequestFor(
        [
            Item("EXP_DROP", "hlxu8700018", booking: "bkg1001", index: 0),
            Item("IMP_PICKUP", "MSCU1234566", index: 1),
            Item("BB_DELIVERY", cargo: "bl-steel-001", index: 2),
            Item("IMP_PICKUP", "MSCU1234566", index: 3),
        ]);

        Assert.Equal(new[] { "HLXU8700018", "MSCU1234566" }, request.Units.ToArray());
        Assert.Equal(new[] { "BKG1001" }, request.Orders.ToArray());
        Assert.Equal(new[] { "BL-STEEL-001" }, request.Bls.ToArray());
    }
}
