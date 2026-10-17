using Yardsyncs.Domain;
using Yardsyncs.Domain.Booking;
using Yardsyncs.Domain.Gate;

namespace Yardsyncs.Application.Edge;

// Edge mode (Yardsyncs Autogate): for terminals whose Yardsyncs runs in the cloud, a small lane agent at the terminal
// keeps the automatic lanes deciding when the link to the cloud drops. It holds a snapshot of what the gate needs
// (lanes, device keys, the coming appointments, drivers' gate passes, the terminal's gate rules), decides with the same
// rules as the cloud, and sends what happened back once the link is up again.

/// <summary>What the lane agent needs to run the lanes on its own for a while. Taken by the cloud every few minutes.</summary>
/// <param name="TimeZone">IANA id of the terminal's time zone.</param>
/// <param name="ValidUntil">Appointments after this aren't in the snapshot: the agent refers trucks for them.</param>
public sealed record EdgeSnapshot(
    int TerminalId,
    string TerminalCode,
    string TerminalName,
    string TimeZone,
    TerminalSettings Settings,
    DateTimeOffset TakenAt,
    DateTimeOffset ValidUntil,
    IReadOnlyList<EdgeLane> Lanes,
    IReadOnlyList<EdgeDevice> Devices,
    IReadOnlyList<EdgeAppointment> Appointments,
    IReadOnlyList<EdgePass> Passes);

public sealed record EdgeLane(string LaneCode, LaneDirection Direction, LaneMode Mode, LaneState State, bool IsActive);

/// <param name="KeyHash">SHA-256 of the device's key (the key itself never leaves the device).</param>
public sealed record EdgeDevice(string DeviceCode, string LaneCode, LaneDeviceKind Kind, string KeyHash);

/// <param name="Plate">The truck's plate, normalized as the gate matches it.</param>
/// <param name="Light">The "ready to go" light at the cloud's last check: the agent can't ask the TOS again.</param>
public sealed record EdgeAppointment(
    long AppointmentId,
    string AppointmentNo,
    string QrToken,
    AppointmentStatus Status,
    DateTimeOffset SlotStart,
    DateTimeOffset SlotEnd,
    string? Plate,
    IReadOnlyList<string> Containers,
    bool HasDriver,
    string? DriverMobile,
    bool TruckBlocked,
    bool DriverBlocked,
    TrafficLight Light,
    string? LightReason);

/// <summary>A driver's gate pass, so the agent can check the rotating code.</summary>
/// <param name="Secret">The pass secret, base64.</param>
public sealed record EdgePass(long PassId, string Secret, string Mobile);

/// <summary>A truck the lane agent decided on while the cloud couldn't be reached.</summary>
/// <param name="PassageId">The agent's own id: sending a passage twice records it once.</param>
/// <param name="Reason">Why it was referred (a trouble reason in UPPER_SNAKE, or EXIT).</param>
/// <param name="PassedAt">The barrier confirmed it opened: the truck went through.</param>
public sealed record EdgePassage(
    Guid PassageId,
    string LaneCode,
    LaneDirection Direction,
    string? AppointmentNo,
    string? Plate,
    IReadOnlyList<string> Containers,
    DateTimeOffset DecidedAt,
    bool Admitted,
    string? Reason,
    string Message,
    DateTimeOffset? PassedAt);

public enum EdgePassageOutcome
{
    /// <summary>Recorded: the visit moved on (gate-in or gate-out at the passage's time), or the referral was logged.</summary>
    Applied,

    /// <summary>Already recorded (sent before, or the visit had moved on meanwhile).</summary>
    Duplicate,

    /// <summary>Not recorded; <see cref="EdgePassageResult.Detail"/> says why. The agent doesn't send it again.</summary>
    Failed,
}

public sealed record EdgePassageResult(Guid PassageId, EdgePassageOutcome Outcome, string? Detail);
