using Tas.Domain;
using Tas.Domain.Booking;

namespace Tas.Application.Tos;

/// <summary>
/// What every TOS adapter must answer — the rules of the TOS adapter kit (docs: "Connecting another TOS").
/// The conformance tests check an adapter against them, and Yardsyncs checks every live answer too: an answer that
/// breaks them is treated as "the TOS could not be reached" (the booking gets a warning and is re-checked), never as
/// a pass. A container on hold must not get through because an adapter forgot a check.
/// </summary>
public static class TosContract
{
    /// <summary>Longest source name (it is stored with every check).</summary>
    public const int MaxSourceLength = 20;

    /// <summary>Longest message (shown to dispatchers and the gate; stored with every check).</summary>
    public const int MaxMessageLength = 400;

    /// <summary>Check types only Yardsyncs itself writes.</summary>
    public static readonly IReadOnlySet<CheckType> ReservedTypes = new HashSet<CheckType> { CheckType.ContainerNumber, CheckType.TosUnavailable };

    /// <summary>The release checks of an import pickup, once the container is known to be there.</summary>
    public static readonly IReadOnlyList<CheckType> ImportReleaseChecks =
        [CheckType.CustomsRelease, CheckType.LineRelease, CheckType.TerminalHold, CheckType.Payment];

    /// <summary>The kind of move, from its move type code and mode (the same split the adapters use).</summary>
    public static MoveKind KindOf(TosItemQuery item)
    {
        var code = item.CategoryCode.ToUpperInvariant();
        return item.Mode == CategoryMode.Cargo ? MoveKind.Cargo
            : code.StartsWith("IMP", StringComparison.Ordinal) ? MoveKind.ImportPickup
            : code.StartsWith("EXP", StringComparison.Ordinal) ? MoveKind.ExportDrop
            : code == "MTY_PICKUP" ? MoveKind.EmptyPickup
            : MoveKind.OtherContainer;
    }

    /// <summary>
    /// The check types an item must get, given what the adapter answered for it:
    ///   import pickup — Availability; unless that fails, also customs, line, terminal hold and payment;
    ///   export drop-off — BookingValid; unless that fails, also Cutoff;
    ///   empty pickup — BookingValid;
    ///   other container moves (empty return…) — TerminalHold;
    ///   cargo (BL / DO) — CargoRelease.
    /// </summary>
    public static IReadOnlyList<CheckType> RequiredChecks(TosItemQuery item, IReadOnlyCollection<TosCheck> answered)
    {
        bool Failed(CheckType type) => answered.Any(c => c.Type == type && c.Result == CheckResult.Fail);
        return KindOf(item) switch
        {
            MoveKind.Cargo => [CheckType.CargoRelease],
            MoveKind.ImportPickup => Failed(CheckType.Availability) ? [CheckType.Availability] : [CheckType.Availability, .. ImportReleaseChecks],
            MoveKind.ExportDrop => Failed(CheckType.BookingValid) ? [CheckType.BookingValid] : [CheckType.BookingValid, CheckType.Cutoff],
            MoveKind.EmptyPickup => [CheckType.BookingValid],
            _ => [CheckType.TerminalHold],
        };
    }

    /// <summary>Everything wrong with an answer to <see cref="ITosAdapter.ValidateAsync"/>; empty when it keeps the rules.</summary>
    public static IReadOnlyList<string> CheckAnswer(IReadOnlyList<TosItemQuery> items, IReadOnlyList<TosCheck>? checks)
    {
        if (checks is null)
            return ["the answer is null (throw TosUnavailableException when the TOS can't answer)"];

        var problems = new List<string>();
        var indexes = items.Select(i => i.Index).ToHashSet();
        foreach (var c in checks)
        {
            if (!indexes.Contains(c.ItemIndex))
                problems.Add($"a {c.Type} check is for item {c.ItemIndex}, which was not asked about");
            if (ReservedTypes.Contains(c.Type))
                problems.Add($"item {c.ItemIndex}: {c.Type} checks are written by Yardsyncs, not by adapters");
            if (c.Result != CheckResult.Pass && string.IsNullOrWhiteSpace(c.Message))
                problems.Add($"item {c.ItemIndex}: the {c.Result} {c.Type} check has no message (dispatchers and the gate need the reason)");
            if (c.Message is { Length: > MaxMessageLength })
                problems.Add($"item {c.ItemIndex}: the {c.Type} message is longer than {MaxMessageLength} characters");
        }

        foreach (var item in items)
        {
            var answered = checks.Where(c => c.ItemIndex == item.Index).ToList();
            var missing = RequiredChecks(item, answered).Where(t => answered.All(c => c.Type != t)).ToList();
            if (missing.Count > 0)
                problems.Add($"item {item.Index} ({item.CategoryCode}): no {string.Join(", ", missing)} check");
        }
        return problems;
    }

    /// <summary>Everything wrong with an answer to <see cref="ITosAdapter.FindBlContainersAsync"/> (null = unknown BL is fine).</summary>
    public static IReadOnlyList<string> CheckBlAnswer(IReadOnlyList<TosBlUnit>? units)
    {
        if (units is null)
            return [];
        var problems = new List<string>();
        foreach (var u in units)
        {
            if (string.IsNullOrWhiteSpace(u.ContainerNo))
                problems.Add("a BL container has no number");
            else if (u.ContainerNo != u.ContainerNo.Trim().ToUpperInvariant())
                problems.Add($"container {u.ContainerNo} must be upper case without spaces");
        }
        foreach (var dup in units.GroupBy(u => u.ContainerNo, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            problems.Add($"container {dup.Key} is listed {dup.Count()} times");
        return problems;
    }

    /// <summary>Everything wrong with an answer to <see cref="ITosAdapter.YardPositionsAsync"/>.</summary>
    public static IReadOnlyList<string> CheckPositions(IReadOnlyList<TosItemQuery> items, IReadOnlyList<TosYardPosition>? positions)
    {
        if (positions is null)
            return ["the answer is null (return an empty list when no position is known)"];
        var problems = new List<string>();
        var indexes = items.Select(i => i.Index).ToHashSet();
        foreach (var p in positions)
        {
            if (!indexes.Contains(p.ItemIndex))
                problems.Add($"a position is for item {p.ItemIndex}, which was not asked about");
            if (p.Block is null && p.Slot is null && p.TransferPoint is null)
                problems.Add($"item {p.ItemIndex}: an empty position (leave the item out instead)");
            foreach (var part in new[] { p.Block, p.Slot, p.TransferPoint })
                if (part is not null && (part.Length == 0 || part != part.Trim()))
                    problems.Add($"item {p.ItemIndex}: '{part}' must be trimmed and not empty (use null for unknown)");
        }
        foreach (var dup in positions.GroupBy(p => p.ItemIndex).Where(g => g.Count() > 1))
            problems.Add($"item {dup.Key} has {dup.Count()} positions");
        return problems;
    }

    /// <summary>Everything wrong with an adapter's source name.</summary>
    public static IReadOnlyList<string> CheckSource(string? source) =>
        string.IsNullOrWhiteSpace(source) ? ["the source name is empty"]
        : source.Length > MaxSourceLength ? [$"the source name '{source}' is longer than {MaxSourceLength} characters"]
        : [];
}

public enum MoveKind
{
    ImportPickup,
    ExportDrop,
    EmptyPickup,
    OtherContainer,
    Cargo,
}
