using Yardsyncs.Domain;
using Yardsyncs.Domain.Gate;

namespace Yardsyncs.Application.Gate;

/// <summary>How a truck can be identified at a lane.</summary>
public enum LaneIdentification
{
    /// <summary>The appointment number, typed by a clerk or at a kiosk.</summary>
    AppointmentNo,

    /// <summary>The rotating gate pass from the driver app, scanned by a clerk or at a kiosk.</summary>
    GatePass,

    /// <summary>The truck's plate, read by a camera with OCR.</summary>
    Plate,

    /// <summary>A container number, read by a camera with OCR.</summary>
    Container,

    /// <summary>An RFID tag on the truck. Recorded per lane now; matching by tag comes with the device adapters.</summary>
    RfidTag,
}

/// <summary>What a lane lacks to work automatically.</summary>
public enum LaneProblem
{
    /// <summary>No barrier to let the truck through.</summary>
    NoBarrier,

    /// <summary>No way to identify trucks without a clerk: no kiosk, and no camera reading plates or containers the terminal matches by.</summary>
    NoIdentification,
}

/// <summary>
/// The Autogate lane model's rules, free of the database: which device reads what, how a lane can identify trucks
/// (its devices, its way of working and the terminal's gate settings), and what a lane still needs to work automatically.
/// </summary>
public static class LaneRules
{
    public const int MaxCodeLength = 30;
    public const int MaxNoteLength = 200;

    /// <summary>What each kind of device may read; the other kinds read nothing.</summary>
    public static IReadOnlySet<LaneRead> ReadsFor(LaneDeviceKind kind) => kind switch
    {
        LaneDeviceKind.Camera or LaneDeviceKind.Ocr => new HashSet<LaneRead>
            { LaneRead.Plate, LaneRead.Container, LaneRead.IsoCode, LaneRead.Seal, LaneRead.Damage },
        LaneDeviceKind.RfidReader => new HashSet<LaneRead> { LaneRead.Tag },
        _ => new HashSet<LaneRead>(),
    };

    /// <summary>"PLATE,CONTAINER" ↔ [Plate, Container]; unknown words are skipped.</summary>
    public static IReadOnlyList<LaneRead> ParseReads(string? stored) =>
        (stored ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(w => Enum.TryParse<LaneRead>(w.Replace("_", ""), ignoreCase: true, out var r) ? (LaneRead?)r : null)
            .Where(r => r is not null)
            .Select(r => r!.Value)
            .Distinct()
            .OrderBy(r => r)
            .ToList();

    public static string? FormatReads(IEnumerable<LaneRead>? reads)
    {
        var list = (reads ?? []).Distinct().OrderBy(r => r).Select(UpperSnake).ToList();
        return list.Count == 0 ? null : string.Join(",", list);
    }

    /// <summary>The problems with a device as entered, or none. The code is required; reads must fit the kind.</summary>
    public static IReadOnlyList<string> Validate(string? deviceCode, LaneDeviceKind kind, IReadOnlyList<LaneRead>? reads)
    {
        var errors = new List<string>();
        var code = deviceCode?.Trim() ?? "";
        if (code.Length is 0 or > MaxCodeLength)
            errors.Add($"The device code is required (max {MaxCodeLength} characters).");
        if (!Enum.IsDefined(kind))
            errors.Add("Unknown device kind.");
        else
        {
            var allowed = ReadsFor(kind);
            var wrong = (reads ?? []).Where(r => !allowed.Contains(r)).Distinct().ToList();
            if (wrong.Count > 0)
                errors.Add(allowed.Count == 0
                    ? $"This {Words(kind)} reads nothing; leave its reads empty."
                    : $"This {Words(kind)} cannot read {string.Join(", ", wrong.Select(r => Words(r)))}.");
        }
        return errors;
    }

    /// <summary>
    /// How trucks can be identified at a lane. A clerk at a manual lane can type the appointment number and scan the
    /// gate pass; a kiosk does the same for the driver; cameras with OCR read plates and container numbers. The
    /// terminal's gate settings switch off finding appointments by number, plate or container; the gate pass always works.
    /// Only active devices count.
    /// </summary>
    public static IReadOnlyList<LaneIdentification> Identifies(LaneMode mode, IEnumerable<GateLaneDevice> devices, TerminalSettings settings)
    {
        var active = devices.Where(d => d.IsActive).ToList();
        var reads = active.SelectMany(d => ParseReads(d.Reads)).ToHashSet();
        var kiosk = active.Any(d => d.Kind == LaneDeviceKind.Kiosk);
        var byHand = mode == LaneMode.Manual || kiosk;

        var result = new List<LaneIdentification>();
        if (byHand && settings.GateByNumber) result.Add(LaneIdentification.AppointmentNo);
        if (byHand) result.Add(LaneIdentification.GatePass);
        if (reads.Contains(LaneRead.Plate) && settings.GateByPlate) result.Add(LaneIdentification.Plate);
        if (reads.Contains(LaneRead.Container) && settings.GateByContainer) result.Add(LaneIdentification.Container);
        if (reads.Contains(LaneRead.Tag)) result.Add(LaneIdentification.RfidTag);
        return result;
    }

    /// <summary>
    /// What a lane still needs before it can work automatically (empty = ready): a barrier to let the truck through, and a
    /// way to identify trucks without a clerk — a kiosk, or a camera reading plates or containers the terminal matches by.
    /// RFID tags don't count yet: the gate does not match by tag until the device adapters come.
    /// </summary>
    public static IReadOnlyList<LaneProblem> AutomaticProblems(IEnumerable<GateLaneDevice> devices, TerminalSettings settings)
    {
        var active = devices.Where(d => d.IsActive).ToList();
        var problems = new List<LaneProblem>();
        if (!active.Any(d => d.Kind == LaneDeviceKind.Barrier))
            problems.Add(LaneProblem.NoBarrier);
        if (!Identifies(LaneMode.Automatic, active, settings).Any(i => i != LaneIdentification.RfidTag))
            problems.Add(LaneProblem.NoIdentification);
        return problems;
    }

    /// <summary>The problems in words, for error messages: "a barrier and a way to identify trucks (…)".</summary>
    public static string Needs(IReadOnlyList<LaneProblem> problems) =>
        string.Join(" and ", problems.Select(p => p switch
        {
            LaneProblem.NoBarrier => "a barrier",
            _ => "a way to identify trucks (a kiosk, or a camera reading plates or container numbers the terminal matches by)",
        }));

    /// <summary>Why a truck can't use the lane now, or null when it is open.</summary>
    public static string? NotOpen(GateLane lane) => lane.State switch
    {
        LaneState.Open => null,
        LaneState.Closed => $"Lane {lane.LaneCode} is closed.{Note(lane)}",
        _ => $"Lane {lane.LaneCode} is out of order.{Note(lane)}",
    };

    private static string Note(GateLane lane) => string.IsNullOrWhiteSpace(lane.StateNote) ? "" : $" {lane.StateNote.Trim()}";

    private static string UpperSnake<T>(T value) where T : struct, Enum =>
        System.Text.RegularExpressions.Regex.Replace(value.ToString(), "([a-z0-9])([A-Z])", "$1_$2").ToUpperInvariant();

    private static string Words<T>(T value) where T : struct, Enum =>
        (object)value switch
        {
            LaneDeviceKind.Ocr => "OCR engine",
            LaneDeviceKind.RfidReader => "RFID reader",
            LaneRead.IsoCode => "ISO codes",
            LaneRead.Plate => "plates",
            LaneRead.Container => "container numbers",
            LaneRead.Seal => "seals",
            LaneRead.Damage => "damage",
            LaneRead.Tag => "tags",
            _ => System.Text.RegularExpressions.Regex.Replace(value.ToString(), "([a-z0-9])([A-Z])", "$1 $2").ToLowerInvariant(),
        };
}
