namespace Yardsyncs.Domain.Gate;

public enum LaneDirection
{
    In,
    Out,
}

public sealed class GateLane
{
    public int LaneId { get; set; }
    public int TerminalId { get; set; }
    public string GateCode { get; set; } = "";
    public string LaneCode { get; set; } = "";
    public LaneDirection Direction { get; set; }
    public bool IsActive { get; set; } = true;
}

public enum GateEventType
{
    OcrRead,
    AppCheckin,
    PassScan,
    Matched,
    NoAppt,
    Early,
    Late,
    GateIn,
    GateOut,
    Trouble,
}

/// <summary>Everything that happens at a lane, for the gate log and turn-time statistics.</summary>
public sealed class GateEvent
{
    public long GateEventId { get; set; }
    public int TerminalId { get; set; }
    public int? LaneId { get; set; }
    public long? AppointmentId { get; set; }
    public GateEventType EventType { get; set; }
    public string? PlateRead { get; set; }
    public string? ContainerRead { get; set; }
    public string? Details { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

public enum TroubleReason
{
    NoAppointment,
    Early,
    Late,
    NotCleared,
    Blocked,
    AlreadyIn,
    Cancelled,

    /// <summary>The driver is not identified as the terminal requires (no driver named, or no gate pass from the driver's app).</summary>
    DriverUnknown,
}

public enum TroubleStatus
{
    Open,
    Resolved,
}

public enum TroubleResolution
{
    Admitted,
    Rejected,
    WalkIn,
    Dismissed,
}

/// <summary>A truck at the gate that could not be admitted automatically, waiting for the trouble desk.</summary>
public sealed class GateTrouble
{
    public long TroubleId { get; set; }
    public int TerminalId { get; set; }
    public int? LaneId { get; set; }
    public long? AppointmentId { get; set; }
    public string? PlateRead { get; set; }
    public string? ContainerReads { get; set; }
    public TroubleReason ReasonCode { get; set; }
    public string ReasonText { get; set; } = "";
    public TroubleStatus Status { get; set; } = TroubleStatus.Open;
    public TroubleResolution? Resolution { get; set; }
    public string? ResolutionNote { get; set; }
    public int? ResolvedBy { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
