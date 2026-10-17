using Yardsyncs.Application.Gate;
using Yardsyncs.Domain;
using Yardsyncs.Domain.Gate;

namespace Yardsyncs.Application.Tests;

public class ExitRulesTests
{
    private static ExitItem Pickup(string c) => new("IMP_PICKUP", CategoryMode.Container, CategoryFlow.Out, c);
    private static ExitItem Drop(string c) => new("EXP_DROP", CategoryMode.Container, CategoryFlow.In, c);

    [Fact]
    public void A_truck_leaving_with_what_it_came_for_is_in_order()
    {
        var check = ExitRules.Check([Pickup("MSCU1234566"), Drop("CSQU3054383")], ["mscu 123456-6"]);
        Assert.Equal(ExitCheckStatus.Ok, check.Status);
        Assert.Equal(new[] { "MSCU1234566" }, check.Seen);
    }

    [Fact]
    public void Missing_pickups_undropped_containers_and_strangers_are_problems()
    {
        var check = ExitRules.Check([Pickup("MSCU1234566"), Drop("CSQU3054383")], ["CSQU3054383", "TGHU8812348", "TGHU8812340"]);
        Assert.Equal(ExitCheckStatus.Problem, check.Status);
        Assert.Equal(
            new[] { (ExitProblemKind.NotOnTruck, "MSCU1234566"), (ExitProblemKind.StillOnTruck, "CSQU3054383") },
            check.Problems.Where(p => p.Kind != ExitProblemKind.NotBooked).Select(p => (p.Kind, p.ContainerNo)));
        // TGHU8812340 fails its check digit: a misread, not a stranger.
        Assert.Equal(new[] { "TGHU8812348" }, check.Problems.Where(p => p.Kind == ExitProblemKind.NotBooked).Select(p => p.ContainerNo));
    }

    [Fact]
    public void Nothing_read_cargo_and_moves_without_a_direction_are_not_checked()
    {
        Assert.Equal(ExitCheckStatus.NotChecked, ExitRules.Check([Pickup("MSCU1234566")], null).Status);
        Assert.Equal(ExitCheckStatus.Ok, ExitRules.Check([new ExitItem("BB_DELIVERY", CategoryMode.Cargo, CategoryFlow.Out, null)], []).Status);
        Assert.Equal(ExitCheckStatus.Ok, ExitRules.Check([new ExitItem("SPECIAL", CategoryMode.Container, null, "MSCU1234566")], ["MSCU1234566"]).Status);
        Assert.Equal(ExitCheckStatus.Ok, ExitRules.Check([new ExitItem("SPECIAL", CategoryMode.Container, null, "MSCU1234566")], []).Status);
    }

    [Fact]
    public void The_cargo_weight_is_the_difference_in_and_out()
    {
        Assert.Equal(24_100m, ExitRules.NetKg(14_900m, 39_000m));
        Assert.Equal(24_100m, ExitRules.NetKg(39_000m, 14_900m));
        Assert.Null(ExitRules.NetKg(null, 39_000m));
    }

    [Fact]
    public void A_move_type_s_direction_follows_its_usual_code()
    {
        Assert.Equal(CategoryFlow.Out, TransactionCategory.GuessFlow("IMP_PICKUP"));
        Assert.Equal(CategoryFlow.Out, TransactionCategory.GuessFlow("bb_delivery"));
        Assert.Equal(CategoryFlow.In, TransactionCategory.GuessFlow("EXP_DROP"));
        Assert.Equal(CategoryFlow.In, TransactionCategory.GuessFlow("MTY_RETURN"));
        Assert.Null(TransactionCategory.GuessFlow("RORO_SHIFT"));
    }
}
