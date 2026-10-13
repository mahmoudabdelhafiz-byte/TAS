using Tas.Application.Booking;
using Tas.Domain.Booking;
using Tas.Domain.Gate;

namespace Tas.Application.Gate;

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

public sealed record LaneView(int LaneId, string GateCode, string LaneCode, LaneDirection Direction, bool IsActive)
{
    public static LaneView From(GateLane l) => new(l.LaneId, l.GateCode, l.LaneCode, l.Direction, l.IsActive);
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
