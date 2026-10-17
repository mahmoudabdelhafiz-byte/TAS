using Yardsyncs.Domain.Booking;
using Yardsyncs.Domain.Gate;

namespace Yardsyncs.Application.Gate;

/// <summary>One piece of damage seen at the gate.</summary>
/// <param name="PhotoId">A photo uploaded to this inspection that shows it.</param>
public sealed record DamageEntryInput(
    DamageSide? Side,
    DamageKind Kind,
    DamageSeverity Severity = DamageSeverity.Minor,
    string? Note = null,
    string? PhotoRef = null,
    long? PhotoId = null);

/// <summary>
/// A clerk's inspection of one container (or, without a container number, the cargo truck) at gate-in or gate-out.
/// The damage list replaces what was recorded before (including what the cameras found).
/// </summary>
public sealed record InspectionInput(
    InspectionStage Stage,
    string? ContainerNo = null,
    string? SealSeen = null,
    bool SealBroken = false,
    bool SealMissing = false,
    string? Notes = null,
    IReadOnlyList<DamageEntryInput>? Damage = null);

/// <summary>The seal number the company declares for a container, before the truck reaches the gate.</summary>
public sealed record SealInput(string? SealNo);

public sealed record DisputeInput(string Reason);

public sealed record ResolveDisputeInput(bool Accept, string Note);

public sealed record DamageEntryView(long DamageId, DamageSide? Side, DamageKind Kind, DamageSeverity Severity, string? Note, string? PhotoRef, long? PhotoId);

public sealed record DisputeView(long DisputeId, DisputeStatus Status, string Reason, DateTimeOffset OpenedAt, string? ResolutionNote, DateTimeOffset? ResolvedAt);

public sealed record InspectionView(
    long InspectionId,
    InspectionStage Stage,
    string? ContainerNo,
    string? SealExpected,
    string? SealSeen,
    SealStatus SealStatus,
    bool DamageFound,
    string? Notes,
    InspectionSource Source,
    DateTimeOffset InspectedAt,
    IReadOnlyList<DamageEntryView> Damage,
    IReadOnlyList<long> PhotoIds,
    DisputeView? Dispute);

/// <summary>A container of the visit with the seal the company declared.</summary>
public sealed record InspectionItem(int Index, string? ContainerNo, string Category, string? SealNo);

/// <summary>Damage recorded at gate-out that the gate-in didn't record: it happened inside, or was missed coming in.</summary>
public sealed record NewDamage(string? ContainerNo, DamageSide? Side, DamageKind Kind, DamageSeverity Severity);

/// <summary>Everything inspected for one visit.</summary>
public sealed record AppointmentInspections(
    string AppointmentNo,
    AppointmentStatus Status,
    string CompanyName,
    string? TruckPlate,
    IReadOnlyList<InspectionItem> Items,
    IReadOnlyList<InspectionView> Inspections,
    IReadOnlyList<NewDamage> NewAtGateOut);

/// <summary>A line in the inspection list.</summary>
public sealed record InspectionRow(
    long InspectionId,
    string AppointmentNo,
    string CompanyName,
    string? ContainerNo,
    InspectionStage Stage,
    DateTimeOffset InspectedAt,
    InspectionSource Source,
    SealStatus SealStatus,
    int DamageCount,
    DisputeStatus? Dispute,
    long? DisputeId);

/// <summary>The inspection rules, free of the database.</summary>
public static class InspectionRules
{
    public const int MaxPhotosPerInspection = 8;
    public const int MaxPhotoBytes = 3 * 1024 * 1024;
    public const int MaxDamagePerInspection = 30;

    public static readonly IReadOnlySet<string> PhotoTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "image/jpeg", "image/png", "image/webp" };

    /// <summary>Seal numbers compared as letters and digits only, upper-case ("ml-449 120" = "ML449120").</summary>
    public static string? NormalizeSeal(string? seal)
    {
        var clean = new string((seal ?? "").Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        return clean.Length == 0 ? null : clean;
    }

    /// <summary>
    /// Missing or broken as the clerk says; otherwise the seal seen against the declared one: the same is Ok, another
    /// number is a Mismatch; seen with nothing declared is Ok (recorded); nothing seen is NotChecked.
    /// </summary>
    public static SealStatus Seal(string? expected, string? seen, bool broken = false, bool missing = false)
    {
        if (missing) return SealStatus.Missing;
        if (broken) return SealStatus.Broken;
        var s = NormalizeSeal(seen);
        if (s is null) return SealStatus.NotChecked;
        var e = NormalizeSeal(expected);
        return e is null || e == s ? SealStatus.Ok : SealStatus.Mismatch;
    }

    /// <summary>
    /// At the entry: the declared seals that the seals read at the gate contradict, as a message for the trouble desk
    /// (null: nothing contradicts). A declared seal among those read is fine; one left over is matched with a read seal
    /// nobody claimed, and that is a mismatch. Nothing read, or fewer seals read than declared, is not held: the cameras
    /// may simply not have seen it (the inspection records it).
    /// </summary>
    public static string? SealProblem(IEnumerable<(string? ContainerNo, string? Declared)> items, IEnumerable<string> sealsRead)
    {
        var seen = sealsRead.Select(NormalizeSeal).Where(s => s is not null).Select(s => s!).Distinct().ToList();
        if (seen.Count == 0)
            return null;
        var declared = items.Select(i => (i.ContainerNo, Seal: NormalizeSeal(i.Declared))).Where(i => i.Seal is not null).ToList();
        var open = new List<(string? ContainerNo, string Seal)>();
        foreach (var item in declared)
        {
            if (!seen.Remove(item.Seal!))
                open.Add((item.ContainerNo, item.Seal!));
        }
        var problems = open.Zip(seen, (d, read) =>
            $"{(d.ContainerNo is null ? "" : $"{d.ContainerNo}: ")}declared {d.Seal}, read {read}").ToList();
        return problems.Count == 0
            ? null
            : $"Seal doesn't match ({string.Join("; ", problems)}). Check the seal before letting the truck in.";
    }

    /// <summary>Damage at gate-out not recorded at gate-in for the same container (same side and kind).</summary>
    public static IReadOnlyList<NewDamage> NewAtGateOut(IEnumerable<(string Container, InspectionStage Stage, IEnumerable<(DamageSide? Side, DamageKind Kind, DamageSeverity Severity)> Damage)> inspections)
    {
        var list = inspections.ToList();
        var atIn = list.Where(i => i.Stage == InspectionStage.In)
            .SelectMany(i => i.Damage.Select(d => (i.Container, d.Side, d.Kind)))
            .ToHashSet();
        return list.Where(i => i.Stage == InspectionStage.Out)
            .SelectMany(i => i.Damage
                .Where(d => !atIn.Contains((i.Container, d.Side, d.Kind)))
                .Select(d => new NewDamage(i.Container.Length == 0 ? null : i.Container, d.Side, d.Kind, d.Severity)))
            .ToList();
    }

    /// <summary>A camera system's free-text side ("rear", "LEFT SIDE", "door") as a side, or null.</summary>
    public static DamageSide? SideFrom(string? text)
    {
        var t = (text ?? "").Trim().ToUpperInvariant();
        foreach (var side in Enum.GetValues<DamageSide>())
            if (t.StartsWith(side.ToString().ToUpperInvariant(), StringComparison.Ordinal))
                return side;
        return t switch
        {
            "BOTTOM" => DamageSide.Underside,
            "ROOF" => DamageSide.Top,
            "BACK" => DamageSide.Rear,
            _ => null,
        };
    }

    /// <summary>Problems with an inspection as entered.</summary>
    public static IReadOnlyList<string> Validate(InspectionInput input)
    {
        var errors = new List<string>();
        if (!Enum.IsDefined(input.Stage)) errors.Add("Stage is In or Out.");
        if (input.SealSeen?.Trim().Length > 20) errors.Add("A seal number is at most 20 characters.");
        if (input.Notes?.Trim().Length > 400) errors.Add("Notes are at most 400 characters.");
        var damage = input.Damage ?? [];
        if (damage.Count > MaxDamagePerInspection) errors.Add($"At most {MaxDamagePerInspection} damage entries.");
        if (damage.Any(d => (d.Side is { } s && !Enum.IsDefined(s)) || !Enum.IsDefined(d.Kind) || !Enum.IsDefined(d.Severity)))
            errors.Add("Unknown side, kind or severity.");
        if (damage.Any(d => d.Note?.Trim().Length > 200 || d.PhotoRef?.Trim().Length > 400))
            errors.Add("Damage notes are at most 200 characters, photo references 400.");
        return errors;
    }
}
