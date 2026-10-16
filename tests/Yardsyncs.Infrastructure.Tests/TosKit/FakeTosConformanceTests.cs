using Microsoft.Extensions.Options;
using Yardsyncs.Application.Tos;
using Yardsyncs.Infrastructure.Tos;

namespace Yardsyncs.Infrastructure.Tests.TosKit;

/// <summary>The demo TOS keeps the TOS contract too (it stands in for a real one in demos and tests).</summary>
public sealed class FakeTosConformanceTests : TosAdapterConformance
{
    private sealed class Options(FakeTosOptions value) : IOptionsMonitor<FakeTosOptions>
    {
        public FakeTosOptions CurrentValue => value;
        public FakeTosOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<FakeTosOptions, string?> listener) => null;
    }

    private static FakeTosOptions Holding(TosScenario s, bool unreachable = false) => new()
    {
        Unreachable = unreachable,
        CustomsHold = [s.CustomsHeldImport],
        NotAvailable = [s.UnknownContainer],
        InvalidBookings = [s.UnknownBooking],
        CargoNotReleased = [s.UnreleasedCargo],
        BillsOfLading = new(StringComparer.OrdinalIgnoreCase) { [s.KnownBl] = [.. s.KnownBlContainers] },
        DefaultBlocks = ["A1", "B2"],
        ReceivalBlocks = new(StringComparer.OrdinalIgnoreCase) { ["EXP_DROP"] = "E1" },
    };

    protected override ITosAdapter Adapter { get; } = new FakeTosAdapter(new Options(Holding(TosScenario.Standard)));

    protected override ITosAdapter UnreachableAdapter { get; } = new FakeTosAdapter(new Options(Holding(TosScenario.Standard, unreachable: true)));

    protected override int TerminalId => 1;
}
