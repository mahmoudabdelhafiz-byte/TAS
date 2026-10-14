using Tas.Domain;
using Tas.Domain.Booking;

namespace Tas.Application.Tos.N4;

/// <summary>
/// Which TAS check an N4 hold belongs to. Hold ids are terminal-specific, so an explicit map from configuration
/// (N4:HoldMapping, e.g. "CUSTOMS_HOLD": "CustomsRelease") wins; otherwise the id is classified by keywords
/// (CUSTOM / FASAH → customs, LINE / CARRIER → line, PAY / UNPAID / INVOICE / STORAGE / DEMURRAGE → payment,
/// anything else → terminal hold).
/// </summary>
public sealed class N4HoldClassifier(IReadOnlyDictionary<string, CheckType>? explicitMap = null)
{
    private readonly Dictionary<string, CheckType> _map =
        new(explicitMap ?? new Dictionary<string, CheckType>(), StringComparer.OrdinalIgnoreCase);

    private static readonly (string Keyword, CheckType Type)[] Keywords =
    [
        ("CUSTOM", CheckType.CustomsRelease),
        ("FASAH", CheckType.CustomsRelease),
        ("LINE", CheckType.LineRelease),
        ("CARRIER", CheckType.LineRelease),
        ("UNPAID", CheckType.Payment),
        ("PAY", CheckType.Payment),
        ("INVOICE", CheckType.Payment),
        ("STORAGE", CheckType.Payment),
        ("DEMURRAGE", CheckType.Payment),
    ];

    public CheckType Classify(string holdId)
    {
        var id = holdId.Trim();
        if (_map.TryGetValue(id, out var mapped))
            return mapped;

        var upper = id.ToUpperInvariant();
        foreach (var (keyword, type) in Keywords)
        {
            if (upper.Contains(keyword, StringComparison.Ordinal))
                return type;
        }
        return CheckType.TerminalHold;
    }
}

/// <summary>
/// Turns what N4 knows about units, orders (bookings / EDOs) and bills of lading into TAS checks.
/// Pure: the N4 call happens elsewhere, so every rule here is unit-tested.
/// </summary>
public sealed class N4CheckEvaluator(N4HoldClassifier holds, TimeZoneInfo terminalTimeZone)
{
    private static readonly HashSet<string> NotYetInTerminal = ["S10_ADVISED", "S20_INBOUND", "S30_ECIN"];
    private static readonly HashSet<string> Gone = ["S50_ECOUT", "S60_LOADED", "S70_DEPARTED", "S99_RETIRED"];
    private static readonly HashSet<string> InTerminal = ["S30_ECIN", "S40_YARD"];

    private static readonly CheckType[] ReleaseChecks =
        [CheckType.CustomsRelease, CheckType.LineRelease, CheckType.TerminalHold, CheckType.Payment];

    public IReadOnlyList<TosCheck> Evaluate(IReadOnlyList<TosItemQuery> items, N4LookupResult n4)
    {
        var units = (n4.Units ?? Array.Empty<N4Unit>()).ToDictionary(u => u.ContainerNo.ToUpperInvariant(), StringComparer.OrdinalIgnoreCase);
        var orders = (n4.Orders ?? Array.Empty<N4Order>()).ToDictionary(o => o.Nbr.ToUpperInvariant(), StringComparer.OrdinalIgnoreCase);
        var bls = (n4.Bls ?? Array.Empty<N4BillOfLading>()).ToDictionary(b => b.Nbr.ToUpperInvariant(), StringComparer.OrdinalIgnoreCase);

        var checks = new List<TosCheck>();
        foreach (var item in items)
        {
            var unit = item.ContainerNo is null ? null : units.GetValueOrDefault(item.ContainerNo);
            var order = item.BookingRef is null ? null : orders.GetValueOrDefault(item.BookingRef);
            var code = item.CategoryCode.ToUpperInvariant();

            if (item.Mode == CategoryMode.Cargo)
                checks.AddRange(Cargo(item, item.CargoRef is null ? null : bls.GetValueOrDefault(item.CargoRef)));
            else if (code.StartsWith("IMP", StringComparison.Ordinal))
                checks.AddRange(ImportPickup(item, unit));
            else if (code.StartsWith("EXP", StringComparison.Ordinal))
                checks.AddRange(ExportDrop(item, unit, order));
            else if (code == "MTY_PICKUP")
                checks.AddRange(EmptyPickup(item, order));
            else if (code == "MTY_RETURN")
                checks.AddRange(EmptyReturn(item, unit));
            else
                checks.AddRange(HoldChecks(item.Index, unit?.Holds, item.ContainerNo, unit?.Gkey, [CheckType.TerminalHold]));
        }
        return checks;
    }

    // ------------------------------------------------------------------ per transaction

    private IEnumerable<TosCheck> ImportPickup(TosItemQuery item, N4Unit? unit)
    {
        var ctr = item.ContainerNo;
        if (unit is null || !unit.Found)
        {
            yield return Fail(item.Index, CheckType.Availability, $"{ctr} is not known in N4");
            yield break;
        }

        var state = unit.TransitState?.ToUpperInvariant();
        if (!string.Equals(unit.Category, "IMPRT", StringComparison.OrdinalIgnoreCase))
            yield return Fail(item.Index, CheckType.Availability, $"{ctr} is not an import container in N4 (category {unit.Category ?? "unknown"})", unit.Gkey);
        else if (state == "S40_YARD")
            yield return Pass(item.Index, CheckType.Availability, unit.Gkey);
        else if (state is not null && NotYetInTerminal.Contains(state))
            yield return Fail(item.Index, CheckType.Availability, $"{ctr} is not in the yard yet (not discharged)", unit.Gkey);
        else if (state is not null && Gone.Contains(state))
            yield return Fail(item.Index, CheckType.Availability, $"{ctr} has already left the terminal", unit.Gkey);
        else
            yield return Warn(item.Index, CheckType.Availability, $"{ctr} has an unexpected N4 state ({unit.TransitState ?? "none"})", unit.Gkey);

        foreach (var check in HoldChecks(item.Index, unit.Holds, ctr, unit.Gkey, ReleaseChecks))
            yield return check;
    }

    private IEnumerable<TosCheck> ExportDrop(TosItemQuery item, N4Unit? unit, N4Order? order)
    {
        var ctr = item.ContainerNo;

        // Booking
        if (item.BookingRef is null)
        {
            yield return Fail(item.Index, CheckType.BookingValid, "An export drop-off needs a booking number");
        }
        else if (order is null || !order.Found)
        {
            yield return Fail(item.Index, CheckType.BookingValid, $"Booking {item.BookingRef} is not known in N4");
        }
        else
        {
            if (order.QuantityRemaining is <= 0)
                yield return Fail(item.Index, CheckType.BookingValid, $"Booking {order.Nbr} has no containers left to receive", order.Gkey);
            else
                yield return Pass(item.Index, CheckType.BookingValid, order.Gkey);

            if (order.CargoCutoff is { } cutoff && item.SlotStart > cutoff)
                yield return Fail(item.Index, CheckType.Cutoff,
                    $"Cargo cut-off for booking {order.Nbr} is {Local(cutoff)}, before this slot", order.Gkey);
            else
                yield return Pass(item.Index, CheckType.Cutoff, order.Gkey);

            foreach (var check in HoldChecks(item.Index, order.Holds, $"booking {order.Nbr}", order.Gkey, []))
                yield return check;
        }

        // Container (only known if pre-advised or previously in the terminal)
        if (unit is { Found: true })
        {
            var state = unit.TransitState?.ToUpperInvariant();
            if (state is not null && InTerminal.Contains(state))
                yield return Fail(item.Index, CheckType.Availability, $"{ctr} is already in the terminal", unit.Gkey);

            if (string.Equals(unit.FreightKind, "FCL", StringComparison.OrdinalIgnoreCase) && unit.VgmKg is null or <= 0)
                yield return Warn(item.Index, CheckType.Vgm, $"No VGM for {ctr} in N4 yet — it must be declared before gate-in", unit.Gkey);
            else
                yield return Pass(item.Index, CheckType.Vgm, unit.Gkey);

            foreach (var check in HoldChecks(item.Index, unit.Holds, ctr, unit.Gkey, []))
                yield return check;
        }
    }

    private IEnumerable<TosCheck> EmptyPickup(TosItemQuery item, N4Order? order)
    {
        if (item.BookingRef is null)
        {
            yield return Fail(item.Index, CheckType.BookingValid, "An empty pickup needs a release / booking number");
            yield break;
        }
        if (order is null || !order.Found)
        {
            yield return Fail(item.Index, CheckType.BookingValid, $"Empty release {item.BookingRef} is not known in N4");
            yield break;
        }

        yield return order.QuantityRemaining is <= 0
            ? Fail(item.Index, CheckType.BookingValid, $"Empty release {order.Nbr} has no containers left", order.Gkey)
            : Pass(item.Index, CheckType.BookingValid, order.Gkey);

        foreach (var check in HoldChecks(item.Index, order.Holds, $"release {order.Nbr}", order.Gkey, []))
            yield return check;
    }

    private IEnumerable<TosCheck> EmptyReturn(TosItemQuery item, N4Unit? unit)
    {
        if (unit is { Found: true } && unit.TransitState?.ToUpperInvariant() is { } state && InTerminal.Contains(state))
            yield return Fail(item.Index, CheckType.Availability, $"{item.ContainerNo} is already in the terminal", unit.Gkey);
        else
            yield return Pass(item.Index, CheckType.Availability, unit?.Gkey);

        foreach (var check in HoldChecks(item.Index, unit?.Holds, item.ContainerNo, unit?.Gkey, [CheckType.TerminalHold]))
            yield return check;
    }

    private IEnumerable<TosCheck> Cargo(TosItemQuery item, N4BillOfLading? bl)
    {
        if (bl is null || !bl.Found)
        {
            yield return Fail(item.Index, CheckType.CargoRelease, $"Cargo {item.CargoRef} is not known in N4");
            yield break;
        }

        yield return bl.Released == false
            ? Fail(item.Index, CheckType.CargoRelease, $"Cargo {bl.Nbr} is not released for delivery", bl.Gkey)
            : Pass(item.Index, CheckType.CargoRelease, bl.Gkey);

        foreach (var check in HoldChecks(item.Index, bl.Holds, $"cargo {bl.Nbr}", bl.Gkey, [CheckType.Payment]))
            yield return check;
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>One FAIL per active hold; a PASS for each of <paramref name="alwaysReport"/> that has no hold.</summary>
    private IEnumerable<TosCheck> HoldChecks(int index, IReadOnlyList<string>? holdIds, string? what, string? tosRef, IReadOnlyList<CheckType> alwaysReport)
    {
        var failed = new HashSet<CheckType>();
        foreach (var hold in (holdIds ?? Array.Empty<string>()).Where(h => !string.IsNullOrWhiteSpace(h)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var type = holds.Classify(hold);
            failed.Add(type);
            yield return Fail(index, type, $"{Label(type)} on {what}: {hold.Trim()}", tosRef);
        }

        foreach (var type in alwaysReport.Where(t => !failed.Contains(t)))
            yield return Pass(index, type, tosRef);
    }

    private static string Label(CheckType type) => type switch
    {
        CheckType.CustomsRelease => "Customs hold",
        CheckType.LineRelease => "Shipping line hold",
        CheckType.Payment => "Unpaid charges",
        CheckType.CargoRelease => "Cargo hold",
        _ => "Terminal hold",
    };

    private string Local(DateTimeOffset t) => TimeZoneInfo.ConvertTime(t, terminalTimeZone).ToString("dd MMM HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    private static TosCheck Pass(int i, CheckType t, string? tosRef = null) => new(i, t, CheckResult.Pass, null, tosRef);
    private static TosCheck Warn(int i, CheckType t, string m, string? tosRef = null) => new(i, t, CheckResult.Warn, m, tosRef);
    private static TosCheck Fail(int i, CheckType t, string m, string? tosRef = null) => new(i, t, CheckResult.Fail, m, tosRef);

    /// <summary>The container numbers, orders and BLs to ask N4 about for these items.</summary>
    public static N4LookupRequest RequestFor(IReadOnlyList<TosItemQuery> items) =>
        new(
            items.Where(i => i.ContainerNo is not null).Select(i => i.ContainerNo!.ToUpperInvariant()).Distinct().ToList(),
            items.Where(i => i.BookingRef is not null && i.Mode == CategoryMode.Container).Select(i => i.BookingRef!.ToUpperInvariant()).Distinct().ToList(),
            items.Where(i => i.CargoRef is not null).Select(i => i.CargoRef!.ToUpperInvariant()).Distinct().ToList());
}
