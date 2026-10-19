using Yardsyncs.Application.Tos;
using Yardsyncs.Application.Zone;
using Yardsyncs.Domain;
using Yardsyncs.Domain.Booking;

namespace Yardsyncs.Application.Tests;

public class ZoneRulesTests
{
    private static readonly DateTimeOffset Slot = new(2026, 10, 21, 10, 0, 0, TimeSpan.FromHours(2));

    private static TosItemQuery Item(int index, string? container, CategoryMode mode = CategoryMode.Container) =>
        new(index, mode == CategoryMode.Cargo ? "BB_DELIVERY" : "IMP_PICKUP", mode, container, null, null, mode == CategoryMode.Cargo ? "BL1" : null, Slot);

    [Fact]
    public void Each_container_gets_the_zones_customs_clearance()
    {
        var items = new[] { Item(1, "MSCU1234566"), Item(2, "caiu 5522102"), Item(3, "MEDU2601003"), Item(4, null, CategoryMode.Cargo) };
        var answers = new[]
        {
            new ZoneClearance("MSCU1234566", ZoneClearanceState.Cleared, null, "ACI-1"),
            new ZoneClearance("CAIU5522102", ZoneClearanceState.NotCleared, "Inspection pending", "ACI-2"),
        };

        var checks = ZoneRules.ClearanceChecks("NAFITH", items, answers);

        Assert.Equal(3, checks.Count); // the cargo item has no container
        Assert.All(checks, c => Assert.Equal(CheckType.CustomsRelease, c.Type));
        Assert.Equal((CheckResult.Pass, "NAFITH:ACI-1"), (checks[0].Result, checks[0].TosRef));
        Assert.Equal(CheckResult.Fail, checks[1].Result);
        Assert.Equal("CAIU5522102: not cleared by customs in NAFITH (Inspection pending)", checks[1].Message);
        Assert.Equal(CheckResult.Warn, checks[2].Result); // the zone doesn't know it yet
        Assert.Contains("re-checked before arrival", checks[2].Message);
    }

    [Fact]
    public void An_unreachable_zone_warns_and_a_missing_permit_is_a_gate_problem()
    {
        var warnings = ZoneRules.UnavailableChecks([Item(1, "MSCU1234566"), Item(2, null, CategoryMode.Cargo)]);
        var warning = Assert.Single(warnings);
        Assert.Equal((1, CheckResult.Warn, ZoneRules.UnavailableMessage), (warning.ItemIndex, warning.Result, warning.Message));

        Assert.Null(ZoneRules.GateProblem("NAFITH", "1234ABJ", new ZonePermit(true, "NP-1", null, null)));
        Assert.Equal("No valid NAFITH entry permit for 1234ABJ: permit expired",
            ZoneRules.GateProblem("NAFITH", "1234ABJ", new ZonePermit(false, null, null, "permit expired")));
        Assert.Equal("No valid NAFITH entry permit for this truck: not found",
            ZoneRules.GateProblem("NAFITH", null, new ZonePermit(false, null, null, null)));
    }

    [Fact]
    public void A_long_reference_fits_the_items_reference()
    {
        var checks = ZoneRules.ClearanceChecks("NAFITH", [Item(1, "MSCU1234566")],
            [new ZoneClearance("MSCU1234566", ZoneClearanceState.Cleared, null, new string('R', 80))]);
        Assert.Equal(50, checks[0].TosRef!.Length);
    }
}
