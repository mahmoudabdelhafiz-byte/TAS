using Microsoft.Extensions.Options;
using Tas.Application.Tos;
using Tas.Domain;
using Tas.Domain.Booking;

namespace Tas.Infrastructure.Tos;

/// <summary>
/// Configurable stand-in for the terminal operating system (section "FakeTos" in appsettings).
/// Everything passes unless it is listed. Use it for development, demos and tests until the N4 adapter exists.
/// </summary>
public sealed class FakeTosOptions
{
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
}

public sealed class FakeTosAdapter(IOptionsMonitor<FakeTosOptions> options) : ITosAdapter
{
    public string Source => "FAKE_TOS";

    public Task<IReadOnlyList<TosCheck>> ValidateAsync(int terminalId, IReadOnlyList<TosItemQuery> items, CancellationToken ct = default)
    {
        var o = options.CurrentValue;
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

    private static bool Listed(IEnumerable<string> list, string? key) =>
        key is not null && list.Contains(key, StringComparer.OrdinalIgnoreCase);
}
