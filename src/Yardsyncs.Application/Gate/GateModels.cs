using Yardsyncs.Application.Booking;
using Yardsyncs.Domain;
using Yardsyncs.Domain.Booking;
using Yardsyncs.Domain.Gate;

namespace Yardsyncs.Application.Gate;

// ---------------------------------------------------------------- inputs

/// <summary>A truck at an entry lane. Send whatever the lane has: plate and container reads from OCR, a QR code, or an appointment number.</summary>
public sealed record ArrivalInput(
    string LaneCode,
    string? PlateRead = null,
    IReadOnlyList<string>? ContainerReads = null,
    string? QrToken = null,
    string? AppointmentNo = null);

public sealed record GateInInput(string AppointmentNo, string? LaneCode = null);

/// <summary>A truck at an exit lane, identified by appointment number or plate read.</summary>
public sealed record GateOutInput(string LaneCode, string? AppointmentNo = null, string? PlateRead = null);

public enum TroubleAction
{
    /// <summary>Let the truck in anyway (note required).</summary>
    Admit,

    /// <summary>Turn the truck away; its appointment becomes REJECTED_AT_GATE and capacity is released (note required).</summary>
    Reject,

    /// <summary>Book a truck that came without an appointment into the current slot and let it in.</summary>
    WalkIn,

    /// <summary>Close without action (false read, truck left).</summary>
    Dismiss,
}

public sealed record WalkInInput(int CompanyId, IReadOnlyList<BookingItemRequest> Items, int? DriverId = null);

public sealed record ResolveTroubleInput(TroubleAction Action, string? Note, string? AppointmentNo = null, WalkInInput? WalkIn = null);

public sealed record LaneInput(string GateCode, string LaneCode, LaneDirection Direction);

/// <summary>Change a lane's gate, way of working or retire it (terminal admins). Leave a field null to keep it.</summary>
public sealed record LaneUpdateInput(string? GateCode = null, LaneMode? Mode = null, bool? IsActive = null);

/// <summary>Open, close or mark a lane out of order (gate staff). A note is required unless the lane is opened.</summary>
public sealed record LaneStateInput(LaneState State, string? Note = null);

/// <summary>A device at a lane. Reads apply to cameras / OCR (plate, container, ISO code, seal, damage) and RFID readers (tag).</summary>
public sealed record LaneDeviceInput(
    string DeviceCode,
    LaneDeviceKind Kind,
    IReadOnlyList<LaneRead>? Reads = null,
    string? Vendor = null,
    string? Model = null,
    string? Address = null,
    bool IsActive = true,
    string? Notes = null);

// ---------------------------------------------------------------- views

public sealed record ArrivalResult(
    GateDecisionKind Decision,
    string Message,
    string? AppointmentNo,
    MatchMethod MatchedBy,
    TrafficLight? Light,
    long? TroubleId,
    TroubleReason? Reason,
    IReadOnlyList<string> Warnings,
    AppointmentDto? Appointment);

public sealed record GateOutResult(
    string AppointmentNo,
    DateTimeOffset? ArrivedAt,
    DateTimeOffset? GateInAt,
    DateTimeOffset GateOutAt,
    double? TurnMinutes,
    double? InTerminalMinutes);

public sealed record TroubleView(
    long TroubleId,
    string? Lane,
    string? AppointmentNo,
    string? PlateRead,
    string? ContainerReads,
    TroubleReason Reason,
    string ReasonText,
    TroubleStatus Status,
    TroubleResolution? Resolution,
    string? Note,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ResolvedAt,
    int? ResolvedBy);

/// <param name="Identifies">How trucks can be identified at this lane (its devices, way of working and the terminal's gate settings).</param>
/// <param name="AutomaticProblems">What the lane still needs to work automatically; empty when it could.</param>
/// <param name="LastRead">The lane's latest device read in the last few minutes, if any.</param>
/// <param name="Transaction">The truck the lane controller is handling or handled last, in the last few minutes.</param>
public sealed record LaneView(
    int LaneId,
    string GateCode,
    string LaneCode,
    LaneDirection Direction,
    bool IsActive,
    LaneState State,
    LaneMode Mode,
    string? StateNote,
    DateTimeOffset? StateChangedAt,
    IReadOnlyList<LaneDeviceView> Devices,
    IReadOnlyList<LaneIdentification> Identifies,
    IReadOnlyList<LaneProblem> AutomaticProblems,
    DeviceReadView? LastRead = null,
    LaneTransactionView? Transaction = null)
{
    public static LaneView From(GateLane l, IReadOnlyList<GateLaneDevice> devices, TerminalSettings settings, DateTimeOffset now,
        DeviceReadView? lastRead = null, LaneTransactionView? transaction = null) => new(
        l.LaneId, l.GateCode, l.LaneCode, l.Direction, l.IsActive, l.State, l.Mode, l.StateNote, l.StateChangedAt,
        devices.OrderBy(d => d.Kind).ThenBy(d => d.DeviceCode).Select(d => LaneDeviceView.From(d, now)).ToList(),
        LaneRules.Identifies(l.Mode, devices, settings),
        LaneRules.AutomaticProblems(devices, settings),
        lastRead,
        transaction);
}

/// <param name="HasKey">A device key has been issued (the device can sign in).</param>
/// <param name="Connection">Whether the device is talking to Yardsyncs now.</param>
public sealed record LaneDeviceView(
    int DeviceId,
    string DeviceCode,
    LaneDeviceKind Kind,
    IReadOnlyList<LaneRead> Reads,
    string? Vendor,
    string? Model,
    string? Address,
    bool IsActive,
    string? Notes,
    bool HasKey = false,
    DeviceConnection Connection = DeviceConnection.NotConnected,
    DateTimeOffset? LastSeenAt = null,
    string? StatusDetail = null)
{
    public static LaneDeviceView From(GateLaneDevice d, DateTimeOffset now) =>
        new(d.DeviceId, d.DeviceCode, d.Kind, LaneRules.ParseReads(d.Reads), d.Vendor, d.Model, d.Address, d.IsActive, d.Notes,
            d.KeyHash is not null, DeviceRules.Connection(d, now), d.LastSeenAt, d.StatusDetail);
}

public sealed record GateBoardTruck(
    string AppointmentNo,
    string CompanyName,
    string? TruckPlate,
    AppointmentStatus Status,
    DateTimeOffset? ArrivedAt,
    DateTimeOffset? GateInAt,
    int MinutesSinceArrival);

/// <summary>The gate supervisor's view right now.</summary>
public sealed record GateBoard(
    IReadOnlyList<GateBoardTruck> AtGate,
    IReadOnlyList<GateBoardTruck> InTerminal,
    int OpenTroubles,
    int CompletedToday,
    double? AverageTurnMinutesToday);
