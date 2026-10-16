using Tas.Application.Tos;
using Tas.Domain;
using Tas.Domain.Booking;

namespace Tas.Application.Tests;

/// <summary>The rules every TOS adapter's answers must keep.</summary>
public class TosContractTests
{
    private static readonly DateTimeOffset Slot = new(2026, 10, 20, 7, 0, 0, TimeSpan.Zero);

    private static TosItemQuery Item(int index, string category, CategoryMode mode = CategoryMode.Container) =>
        new(index, category, mode, "MSCU1234566", "BK1", null, mode == CategoryMode.Cargo ? "BL1" : null, Slot);

    private static TosCheck Pass(int index, CheckType type) => new(index, type, CheckResult.Pass, null);
    private static TosCheck Fail(int index, CheckType type) => new(index, type, CheckResult.Fail, "reason");

    [Theory]
    [InlineData("IMP_PICKUP", CategoryMode.Container, MoveKind.ImportPickup)]
    [InlineData("imp_reefer", CategoryMode.Container, MoveKind.ImportPickup)]
    [InlineData("EXP_DROP", CategoryMode.Container, MoveKind.ExportDrop)]
    [InlineData("MTY_PICKUP", CategoryMode.Container, MoveKind.EmptyPickup)]
    [InlineData("MTY_RETURN", CategoryMode.Container, MoveKind.OtherContainer)]
    [InlineData("IMP_CARGO", CategoryMode.Cargo, MoveKind.Cargo)]
    public void Moves_are_sorted_by_kind(string category, CategoryMode mode, MoveKind kind) =>
        Assert.Equal(kind, TosContract.KindOf(Item(0, category, mode)));

    [Fact]
    public void A_complete_answer_keeps_the_rules()
    {
        IReadOnlyList<TosItemQuery> items = [Item(4, "IMP_PICKUP"), Item(1, "EXP_DROP"), Item(2, "MTY_PICKUP"), Item(3, "MTY_RETURN"), Item(9, "BB", CategoryMode.Cargo)];
        IReadOnlyList<TosCheck> checks =
        [
            Pass(4, CheckType.Availability), Pass(4, CheckType.CustomsRelease), Fail(4, CheckType.LineRelease), Pass(4, CheckType.TerminalHold), Pass(4, CheckType.Payment),
            Pass(1, CheckType.BookingValid), Pass(1, CheckType.Cutoff), Pass(1, CheckType.Vgm),
            Pass(2, CheckType.BookingValid),
            Pass(3, CheckType.TerminalHold),
            Pass(9, CheckType.CargoRelease),
        ];
        Assert.Empty(TosContract.CheckAnswer(items, checks));
    }

    [Fact]
    public void A_failed_first_check_is_enough()
    {
        // Not in the yard: no need to report holds. Unknown booking: no cut-off to report.
        Assert.Empty(TosContract.CheckAnswer([Item(0, "IMP_PICKUP")], [Fail(0, CheckType.Availability)]));
        Assert.Empty(TosContract.CheckAnswer([Item(0, "EXP_DROP")], [Fail(0, CheckType.BookingValid)]));
    }

    [Fact]
    public void A_forgotten_check_is_a_breach()
    {
        // The container is there, but nobody said whether customs released it: it must not pass.
        var problems = TosContract.CheckAnswer([Item(0, "IMP_PICKUP")],
            [Pass(0, CheckType.Availability), Pass(0, CheckType.LineRelease), Pass(0, CheckType.TerminalHold), Pass(0, CheckType.Payment)]);
        Assert.Contains("item 0 (IMP_PICKUP): no CustomsRelease check", Assert.Single(problems));
        Assert.Contains("no Cutoff", Assert.Single(TosContract.CheckAnswer([Item(0, "EXP_DROP")], [Pass(0, CheckType.BookingValid)])));
        Assert.Contains("no CargoRelease", Assert.Single(TosContract.CheckAnswer([Item(0, "BB", CategoryMode.Cargo)], [])));
        Assert.Contains("is null", Assert.Single(TosContract.CheckAnswer([Item(0, "MTY_RETURN")], null)));
    }

    [Fact]
    public void Answers_stay_within_what_was_asked_and_say_why()
    {
        IReadOnlyList<TosItemQuery> items = [Item(0, "MTY_RETURN")];
        Assert.Contains(TosContract.CheckAnswer(items, [Pass(0, CheckType.TerminalHold), Pass(5, CheckType.TerminalHold)]), p => p.Contains("item 5, which was not asked about"));
        Assert.Contains(TosContract.CheckAnswer(items, [Pass(0, CheckType.TerminalHold), Pass(0, CheckType.ContainerNumber)]), p => p.Contains("written by Yardsyncs"));
        Assert.Contains(TosContract.CheckAnswer(items, [new TosCheck(0, CheckType.TerminalHold, CheckResult.Warn, " ")]), p => p.Contains("has no message"));
        Assert.Contains(TosContract.CheckAnswer(items, [new TosCheck(0, CheckType.TerminalHold, CheckResult.Fail, new string('x', 401))]), p => p.Contains("longer than 400"));
    }

    [Fact]
    public void Bill_of_lading_lists_are_clean()
    {
        Assert.Empty(TosContract.CheckBlAnswer(null));
        Assert.Empty(TosContract.CheckBlAnswer([new TosBlUnit("MSCU1234566", "22G1"), new TosBlUnit("MSCU5523417", null)]));
        Assert.Contains(TosContract.CheckBlAnswer([new TosBlUnit("mscu1234566", null)]), p => p.Contains("upper case"));
        Assert.Contains(TosContract.CheckBlAnswer([new TosBlUnit("MSCU1234566", null), new TosBlUnit("MSCU1234566", null)]), p => p.Contains("2 times"));
        Assert.Contains(TosContract.CheckBlAnswer([new TosBlUnit(" ", null)]), p => p.Contains("no number"));
    }

    [Fact]
    public void Yard_positions_are_clean()
    {
        IReadOnlyList<TosItemQuery> items = [Item(0, "IMP_PICKUP"), Item(1, "EXP_DROP")];
        Assert.Empty(TosContract.CheckPositions(items, [new TosYardPosition(0, "B2", "14.03.2", null), new TosYardPosition(1, "E1", null, null)]));
        Assert.Contains(TosContract.CheckPositions(items, [new TosYardPosition(7, "B2", null, null)]), p => p.Contains("not asked about"));
        Assert.Contains(TosContract.CheckPositions(items, [new TosYardPosition(0, null, null, null)]), p => p.Contains("empty position"));
        Assert.Contains(TosContract.CheckPositions(items, [new TosYardPosition(0, " B2", null, null)]), p => p.Contains("trimmed"));
        Assert.Contains(TosContract.CheckPositions(items, [new TosYardPosition(0, "B2", null, null), new TosYardPosition(0, "B3", null, null)]), p => p.Contains("2 positions"));
    }

    [Fact]
    public void Source_names_are_short()
    {
        Assert.Empty(TosContract.CheckSource("N4"));
        Assert.NotEmpty(TosContract.CheckSource(""));
        Assert.NotEmpty(TosContract.CheckSource("A_VERY_LONG_TOS_NAME_INDEED"));
    }
}
