using Microsoft.Extensions.Options;
using Yardsyncs.Application.Tos;
using Yardsyncs.Domain;
using Yardsyncs.Domain.Booking;

namespace Yardsyncs.Infrastructure.Tos;

/// <summary>
/// Configurable stand-in for the terminal operating system (section "FakeTos" in appsettings).
/// Everything passes unless it is listed. Use it for development, demos and tests until the N4 adapter exists.
/// </summary>
public sealed class FakeTosOptions
{
    /// <summary>Act as a TOS that can't be reached (every call fails), to try the warning and re-check flow.</summary>
    public bool Unreachable { get; set; }

    /// <summary>Import containers not yet discharged / not in the yard.</summary>
    public List<string> NotAvailable { get; set; } = [];

    public List<string> CustomsHold { get; set; } = [];
    public List<string> LineHold { get; set; } = [];
    public List<string> TerminalHold { get; set; } = [];

    /// <summary>Containers or cargo references with unpaid terminal charges (FAIL).</summary>
    public List<string> Unpaid { get; set; } = [];

    /// <summary>Containers with a payment warning, e.g. storage about to start (WARN).</summary>
    public List<string> PaymentWarning { get; set; } = [];

    /// <summary>Export / empty-release booking references that do not exist or are cancelled.</summary>
    public List<string> InvalidBookings { get; set; } = [];

    /// <summary>Export bookings whose cut-off has passed.</summary>
    public List<string> ClosedBookings { get; set; } = [];

    /// <summary>Export containers with no VGM declared.</summary>
    public List<string> MissingVgm { get; set; } = [];

    /// <summary>Cargo references (BL / DO) not released by customs.</summary>
    public List<string> CargoNotReleased { get; set; } = [];

    /// <summary>Yard positions of containers, as "BLOCK.ROW.BAY.TIER" (e.g. "B2.14.03.2"). Unlisted imports get a made-up slot in <see cref="DefaultBlocks"/>.</summary>
    public Dictionary<string, string> YardPositions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Blocks used for unlisted import / empty pickups.</summary>
    public List<string> DefaultBlocks { get; set; } = [];

    /// <summary>Block where deliveries of a move type go, e.g. EXP_DROP → E1, MTY_RETURN → M1.</summary>
    public Dictionary<string, string> ReceivalBlocks { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Import bills of lading and their containers, as "CONTAINER" or "CONTAINER:ISOTYPE" (e.g. "MSCU1234566:22G1").</summary>
    public Dictionary<string, List<string>> BillsOfLading { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class FakeTosAdapter(IOptionsMonitor<FakeTosOptions> options) : ITosAdapter
{
    public string Source => "FAKE_TOS";

    private FakeTosOptions Current =>
        options.CurrentValue is { Unreachable: true } ? throw new TosUnavailableException("The demo TOS is set to unreachable (FakeTos:Unreachable).") : options.CurrentValue;

    public Task<IReadOnlyList<TosCheck>> ValidateAsync(int terminalId, IReadOnlyList<TosItemQuery> items, CancellationToken ct = default)
    {
        var o = Current;
        var checks = new List<TosCheck>();

        foreach (var item in items)
        {
            var code = item.CategoryCode.ToUpperInvariant();
            var container = item.ContainerNo;

            void Check(CheckType type, IEnumerable<string> failList, string? key, string failMessage) =>
                checks.Add(Listed(failList, key)
                    ? new TosCheck(item.Index, type, CheckResult.Fail, failMessage)
                    : new TosCheck(item.Index, type, CheckResult.Pass, null));

            if (item.Mode == CategoryMode.Cargo)
            {
                Check(CheckType.CargoRelease, o.CargoNotReleased, item.CargoRef, $"Cargo {item.CargoRef} is not released by customs");
                Check(CheckType.Payment, o.Unpaid, item.CargoRef, $"Charges for {item.CargoRef} are unpaid");
                continue;
            }

            if (code.StartsWith("IMP", StringComparison.Ordinal))
            {
                Check(CheckType.Availability, o.NotAvailable, container, $"{container} is not available for pickup yet");
                Check(CheckType.CustomsRelease, o.CustomsHold, container, $"Customs hold on {container}");
                Check(CheckType.LineRelease, o.LineHold, container, $"Shipping line has not released {container}");
                Check(CheckType.TerminalHold, o.TerminalHold, container, $"Terminal hold on {container}");

                if (Listed(o.Unpaid, container))
                    checks.Add(new TosCheck(item.Index, CheckType.Payment, CheckResult.Fail, $"Charges for {container} are unpaid"));
                else if (Listed(o.PaymentWarning, container))
                    checks.Add(new TosCheck(item.Index, CheckType.Payment, CheckResult.Warn, $"Storage charges on {container} start today"));
                else
                    checks.Add(new TosCheck(item.Index, CheckType.Payment, CheckResult.Pass, null));
            }
            else if (code.StartsWith("EXP", StringComparison.Ordinal))
            {
                Check(CheckType.BookingValid, o.InvalidBookings, item.BookingRef, $"Booking {item.BookingRef} is not valid");
                Check(CheckType.Cutoff, o.ClosedBookings, item.BookingRef, $"Cut-off has passed for booking {item.BookingRef}");
                Check(CheckType.Vgm, o.MissingVgm, container, $"No VGM declared for {container}");
            }
            else if (code == "MTY_PICKUP")
            {
                Check(CheckType.BookingValid, o.InvalidBookings, item.BookingRef, $"Empty release {item.BookingRef} is not valid");
            }
            else
            {
                Check(CheckType.TerminalHold, o.TerminalHold, container, $"Terminal hold on {container}");
            }
        }

        return Task.FromResult<IReadOnlyList<TosCheck>>(checks);
    }

    public Task<IReadOnlyList<TosBlUnit>?> FindBlContainersAsync(int terminalId, string blNo, CancellationToken ct = default)
    {
        var bls = Current.BillsOfLading;
        var key = bls.Keys.FirstOrDefault(k => string.Equals(k, blNo.Trim(), StringComparison.OrdinalIgnoreCase));
        if (key is null)
            return Task.FromResult<IReadOnlyList<TosBlUnit>?>(null);

        IReadOnlyList<TosBlUnit> units = bls[key]
            .Select(entry => entry.Split(':', 2, StringSplitOptions.TrimEntries))
            .Select(parts => new TosBlUnit(parts[0].ToUpperInvariant(), parts.Length > 1 && parts[1].Length > 0 ? parts[1].ToUpperInvariant() : null))
            .ToList();
        return Task.FromResult<IReadOnlyList<TosBlUnit>?>(units);
    }

    public Task<IReadOnlyList<TosYardPosition>> YardPositionsAsync(int terminalId, IReadOnlyList<TosItemQuery> items, CancellationToken ct = default)
    {
        var o = Current;
        var result = new List<TosYardPosition>();
        foreach (var item in items)
        {
            var code = item.CategoryCode.ToUpperInvariant();
            if (item.ContainerNo is { } container && o.YardPositions.TryGetValue(container, out var listed))
            {
                var parts = listed.Split('.', 2);
                result.Add(new TosYardPosition(item.Index, parts[0], parts.Length > 1 ? parts[1] : null, null));
            }
            else if (o.ReceivalBlocks.TryGetValue(code, out var block))
            {
                result.Add(new TosYardPosition(item.Index, block, null, null));
            }
            else if (item.ContainerNo is { Length: >= 11 } c && o.DefaultBlocks.Count > 0)
            {
                // Stable made-up slot from the container's digits, so demos show the same place every time.
                var digits = c[4..10];
                var n = digits.Sum(ch => ch - '0');
                result.Add(new TosYardPosition(item.Index, o.DefaultBlocks[n % o.DefaultBlocks.Count],
                    $"{1 + n % 20:00}.{1 + (n / 3) % 12:00}.{1 + n % 4}", null));
            }
        }
        return Task.FromResult<IReadOnlyList<TosYardPosition>>(result);
    }

    private static bool Listed(IEnumerable<string> list, string? key) =>
        key is not null && list.Contains(key, StringComparer.OrdinalIgnoreCase);
}
