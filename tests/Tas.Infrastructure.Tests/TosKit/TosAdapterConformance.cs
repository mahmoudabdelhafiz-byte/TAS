using Tas.Application.Tos;
using Tas.Domain;
using Tas.Domain.Booking;

namespace Tas.Infrastructure.Tests.TosKit;

/// <summary>
/// What the test TOS holds, so the conformance tests know what to expect. Give each adapter's test TOS (a stub of
/// its web service, a test system) these units, bookings and bills of lading.
/// </summary>
/// <param name="ReadyImport">An import container in the yard: no holds, charges paid.</param>
/// <param name="CustomsHeldImport">An import container in the yard with a customs hold.</param>
/// <param name="UnknownContainer">A container the TOS doesn't know.</param>
/// <param name="ExportBooking">An open export booking whose cut-off is after <paramref name="SlotStart"/>.</param>
/// <param name="ExportContainer">A container to drop off on that booking (the TOS may not know it yet).</param>
/// <param name="UnknownBooking">A booking number the TOS doesn't know.</param>
/// <param name="EmptyRelease">An empty release with containers left.</param>
/// <param name="KnownBl">An import bill of lading and its containers.</param>
/// <param name="UnknownBl">A bill of lading the TOS doesn't know.</param>
/// <param name="ReleasedCargo">A cargo reference (BL / DO) released for delivery.</param>
/// <param name="UnreleasedCargo">A cargo reference not released.</param>
public sealed record TosScenario(
    string ReadyImport,
    string CustomsHeldImport,
    string UnknownContainer,
    string ExportBooking,
    string ExportContainer,
    string UnknownBooking,
    string EmptyRelease,
    string KnownBl,
    IReadOnlyList<string> KnownBlContainers,
    string UnknownBl,
    string ReleasedCargo,
    string UnreleasedCargo,
    DateTimeOffset SlotStart)
{
    public static readonly TosScenario Standard = new(
        ReadyImport: "MSCU1234566",
        CustomsHeldImport: "MSCU7654329",
        UnknownContainer: "MEDU2601003",
        ExportBooking: "EXP26001",
        ExportContainer: "CMAU5100028",
        UnknownBooking: "NOSUCH999",
        EmptyRelease: "MTY26001",
        KnownBl: "MEDUJD260001",
        KnownBlContainers: ["MSCU1234566", "MSCU5523417"],
        UnknownBl: "NOSUCHBL1",
        ReleasedCargo: "BL-REL-1",
        UnreleasedCargo: "BL-HOLD-1",
        SlotStart: new DateTimeOffset(2026, 10, 20, 7, 0, 0, TimeSpan.Zero));
}

/// <summary>
/// The TOS adapter kit's conformance tests. Every TOS adapter has a test class deriving from this one (see
/// FakeTosConformanceTests, N4ConformanceTests); a new adapter is ready when all of them pass. The rules are those of
/// <see cref="TosContract"/>, which Yardsyncs also applies to every live answer.
/// </summary>
public abstract class TosAdapterConformance : IAsyncLifetime
{
    /// <summary>The adapter, connected to a test TOS holding <see cref="Scenario"/>.</summary>
    protected abstract ITosAdapter Adapter { get; }

    /// <summary>The same adapter when its TOS can't be reached (timeout, error answer).</summary>
    protected abstract ITosAdapter UnreachableAdapter { get; }

    protected abstract int TerminalId { get; }

    protected virtual TosScenario Scenario => TosScenario.Standard;

    /// <summary>False skips the tests (e.g. no test database on this machine).</summary>
    protected virtual bool Enabled => true;

    public virtual Task InitializeAsync() => Task.CompletedTask;

    public virtual Task DisposeAsync() => Task.CompletedTask;

    private TosItemQuery Item(int index, string category, string? container = null, string? booking = null, string? cargo = null) =>
        new(index, category, cargo is null ? CategoryMode.Container : CategoryMode.Cargo, container, booking, null, cargo, Scenario.SlotStart);

    /// <summary>One of each kind of move, with item numbers out of order (an appointment's items keep their own).</summary>
    private IReadOnlyList<TosItemQuery> EveryKindOfMove() =>
    [
        Item(7, "IMP_PICKUP", Scenario.ReadyImport),
        Item(3, "IMP_PICKUP", Scenario.CustomsHeldImport),
        Item(11, "IMP_PICKUP", Scenario.UnknownContainer),
        Item(0, "EXP_DROP", Scenario.ExportContainer, booking: Scenario.ExportBooking),
        Item(5, "EXP_DROP", Scenario.ExportContainer, booking: Scenario.UnknownBooking),
        Item(2, "MTY_PICKUP", booking: Scenario.EmptyRelease),
        Item(9, "MTY_PICKUP", booking: Scenario.UnknownBooking),
        Item(8, "MTY_RETURN", Scenario.UnknownContainer),
        Item(4, "BB_DELIVERY", cargo: Scenario.ReleasedCargo),
        Item(6, "BB_DELIVERY", cargo: Scenario.UnreleasedCargo),
    ];

    private async Task<IReadOnlyList<TosCheck>> ChecksFor(params TosItemQuery[] items) => await Adapter.ValidateAsync(TerminalId, items);

    [Fact]
    public void The_source_name_is_set_and_short()
    {
        if (!Enabled) return;
        Assert.Empty(TosContract.CheckSource(Adapter.Source));
        Assert.Empty(TosContract.CheckSource(Adapter.SourceFor(TerminalId)));
    }

    [Fact]
    public async Task Every_move_gets_the_checks_it_needs()
    {
        if (!Enabled) return;
        var items = EveryKindOfMove();
        var checks = await Adapter.ValidateAsync(TerminalId, items);
        Assert.Empty(TosContract.CheckAnswer(items, checks));
    }

    [Fact]
    public async Task A_ready_import_container_passes()
    {
        if (!Enabled) return;
        var checks = await ChecksFor(Item(1, "IMP_PICKUP", Scenario.ReadyImport));
        Assert.DoesNotContain(checks, c => c.Result == CheckResult.Fail);
        Assert.Contains(checks, c => c.Type == CheckType.Availability && c.Result == CheckResult.Pass);
    }

    [Fact]
    public async Task A_customs_hold_fails_with_its_reason()
    {
        if (!Enabled) return;
        var checks = await ChecksFor(Item(1, "IMP_PICKUP", Scenario.CustomsHeldImport));
        var hold = Assert.Single(checks, c => c.Type == CheckType.CustomsRelease && c.Result == CheckResult.Fail);
        Assert.False(string.IsNullOrWhiteSpace(hold.Message));
    }

    [Fact]
    public async Task An_unknown_container_cannot_be_picked_up()
    {
        if (!Enabled) return;
        Assert.Contains(await ChecksFor(Item(1, "IMP_PICKUP", Scenario.UnknownContainer)), c => c.Result == CheckResult.Fail);
    }

    [Fact]
    public async Task Container_numbers_match_whatever_their_case()
    {
        if (!Enabled) return;
        var checks = await ChecksFor(Item(1, "IMP_PICKUP", Scenario.CustomsHeldImport.ToLowerInvariant()));
        Assert.Contains(checks, c => c.Type == CheckType.CustomsRelease && c.Result == CheckResult.Fail);
    }

    [Fact]
    public async Task Bookings_are_checked()
    {
        if (!Enabled) return;
        var checks = await ChecksFor(
            Item(1, "EXP_DROP", Scenario.ExportContainer, booking: Scenario.ExportBooking),
            Item(2, "EXP_DROP", Scenario.ExportContainer, booking: Scenario.UnknownBooking),
            Item(3, "MTY_PICKUP", booking: Scenario.EmptyRelease),
            Item(4, "MTY_PICKUP", booking: Scenario.UnknownBooking));
        CheckResult Booking(int index) => Assert.Single(checks, c => c.ItemIndex == index && c.Type == CheckType.BookingValid).Result;
        Assert.Equal(CheckResult.Pass, Booking(1));
        Assert.Equal(CheckResult.Fail, Booking(2));
        Assert.Equal(CheckResult.Pass, Booking(3));
        Assert.Equal(CheckResult.Fail, Booking(4));
        Assert.Contains(checks, c => c.ItemIndex == 1 && c.Type == CheckType.Cutoff && c.Result == CheckResult.Pass);
    }

    [Fact]
    public async Task Cargo_releases_are_checked()
    {
        if (!Enabled) return;
        var checks = await ChecksFor(Item(1, "BB_DELIVERY", cargo: Scenario.ReleasedCargo), Item(2, "BB_DELIVERY", cargo: Scenario.UnreleasedCargo));
        Assert.Equal(CheckResult.Pass, Assert.Single(checks, c => c.ItemIndex == 1 && c.Type == CheckType.CargoRelease).Result);
        Assert.Equal(CheckResult.Fail, Assert.Single(checks, c => c.ItemIndex == 2 && c.Type == CheckType.CargoRelease).Result);
    }

    [Fact]
    public async Task No_moves_no_checks()
    {
        if (!Enabled) return;
        Assert.Empty(await Adapter.ValidateAsync(TerminalId, []));
        Assert.Empty(await Adapter.YardPositionsAsync(TerminalId, []));
    }

    [Fact]
    public async Task A_bill_of_lading_lists_its_containers()
    {
        if (!Enabled) return;
        var units = await Adapter.FindBlContainersAsync(TerminalId, Scenario.KnownBl.ToLowerInvariant());
        Assert.NotNull(units);
        Assert.Empty(TosContract.CheckBlAnswer(units));
        Assert.Equal(Scenario.KnownBlContainers.Order(), units.Select(u => u.ContainerNo).Order());
        Assert.Null(await Adapter.FindBlContainersAsync(TerminalId, Scenario.UnknownBl));
    }

    [Fact]
    public async Task Yard_positions_keep_the_rules()
    {
        if (!Enabled) return;
        var items = EveryKindOfMove();
        var positions = await Adapter.YardPositionsAsync(TerminalId, items);
        Assert.Empty(TosContract.CheckPositions(items, positions));
    }

    [Fact]
    public async Task An_unreachable_tos_says_so()
    {
        if (!Enabled) return;
        var items = EveryKindOfMove();
        await Assert.ThrowsAsync<TosUnavailableException>(() => UnreachableAdapter.ValidateAsync(TerminalId, items));
        await Assert.ThrowsAsync<TosUnavailableException>(() => UnreachableAdapter.FindBlContainersAsync(TerminalId, Scenario.KnownBl));
        await Assert.ThrowsAsync<TosUnavailableException>(() => UnreachableAdapter.YardPositionsAsync(TerminalId, items));
    }
}
