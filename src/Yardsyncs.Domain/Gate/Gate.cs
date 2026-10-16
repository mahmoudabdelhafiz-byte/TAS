namespace Yardsyncs.Domain.Gate;

public enum LaneDirection
{
    In,
    Out,
}

/// <summary>Whether trucks can use a lane right now; set by the gate staff.</summary>
public enum LaneState
{
    Open,
    Closed,
    OutOfOrder,
}

/// <summary>How a lane works: a clerk decides at the lane, or the lane decides itself and refers exceptions.</summary>
public enum LaneMode
{
    Manual,
    Automatic,
}

public sealed class GateLane
{
    public int LaneId { get; set; }
    public int TerminalId { get; set; }
    public string GateCode { get; set; } = "";
    public string LaneCode { get; set; } = "";
    public LaneDirection Direction { get; set; }

    /// <summary>False when the lane is retired (kept for the gate log); <see cref="State"/> is the day-to-day open / closed.</summary>
    public bool IsActive { get; set; } = true;

    public LaneState State { get; set; } = LaneState.Open;
    public LaneMode Mode { get; set; } = LaneMode.Manual;
    public string? StateNote { get; set; }
    public DateTimeOffset? StateChangedAt { get; set; }
    public int? StateChangedBy { get; set; }
}

/// <summary>The kinds of equipment at a lane. Vendor-neutral: the vendor and model are recorded, the kind decides the role.</summary>
public enum LaneDeviceKind
{
    Camera,
    Ocr,
    RfidReader,
    Kiosk,
    Barrier,
    TrafficLight,
    Display,
    Printer,
    Intercom,
    Weighbridge,
}

/// <summary>What a camera, OCR engine or RFID reader reads.</summary>
public enum LaneRead
{
    Plate,
    Container,
    IsoCode,
    Seal,
    Damage,
    Tag,
}

/// <summary>A device at a gate lane.</summary>
public sealed class GateLaneDevice
{
    public int DeviceId { get; set; }
    public int TerminalId { get; set; }
    public int LaneId { get; set; }
    public string DeviceCode { get; set; } = "";
    public LaneDeviceKind Kind { get; set; }

    /// <summary>Comma-separated <see cref="LaneRead"/> values in upper snake case (PLATE,CONTAINER); null for devices that read nothing.</summary>
    public string? Reads { get; set; }

    public string? Vendor { get; set; }
    public string? Model { get; set; }
    public string? Address { get; set; }
    public bool IsActive { get; set; } = true;
    public string? Notes { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>SHA-256 (hex) of the device's key; the key itself is shown once when issued.</summary>
    public string? KeyHash { get; set; }

    public DateTimeOffset? KeySetAt { get; set; }

    /// <summary>The device's last contact with Yardsyncs (any call with its key).</summary>
    public DateTimeOffset? LastSeenAt { get; set; }

    public DeviceHealth? ReportedStatus { get; set; }
    public string? StatusDetail { get; set; }
}

/// <summary>What a device says about itself.</summary>
public enum DeviceHealth
{
    Ok,
    Fault,
}

/// <summary>Commands for lane devices: barrier open / close, light red / green / off, display show / clear, ticket print.</summary>
public enum LaneCommand
{
    Open,
    Close,
    Red,
    Green,
    Off,
    Show,
    Clear,
    Print,
}

public enum CommandResult
{
    Done,
    Failed,
}

/// <summary>One read from a lane device: plate, containers, seals, damage photos, RFID tag, kiosk entry or weight.</summary>
public sealed class LaneDeviceRead
{
    public long ReadId { get; set; }
    public int TerminalId { get; set; }
    public int LaneId { get; set; }
    public int DeviceId { get; set; }
    public DateTimeOffset ReadAt { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }

    /// <summary>Normalized plate.</summary>
    public string? Plate { get; set; }

    public short? PlateConfidence { get; set; }

    /// <summary>The first container read, normalized.</summary>
    public string? ContainerNo { get; set; }

    /// <summary>The whole read as JSON, normalized.</summary>
    public string Payload { get; set; } = "{}";

    /// <summary>The truck transaction the lane controller filed this read under.</summary>
    public long? TransactionId { get; set; }
}

/// <summary>A command waiting for, delivered to or carried out by a lane device.</summary>
public sealed class LaneDeviceCommand
{
    public long CommandId { get; set; }
    public int TerminalId { get; set; }
    public int LaneId { get; set; }
    public int DeviceId { get; set; }
    public LaneCommand Command { get; set; }
    public string? Text { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>The user who sent it; null when the lane itself did.</summary>
    public int? CreatedBy { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public DateTimeOffset? DoneAt { get; set; }
    public CommandResult? Result { get; set; }
    public string? ResultDetail { get; set; }

    /// <summary>The truck transaction this command belongs to (barrier opened for that truck…).</summary>
    public long? TransactionId { get; set; }
}

/// <summary>Where a truck's passage through a lane stands.</summary>
public enum LaneTransactionStatus
{
    /// <summary>Gathering reads.</summary>
    Open,

    /// <summary>Let in (or out): barrier opening.</summary>
    Admitted,

    /// <summary>A clerk has to help.</summary>
    Referred,

    /// <summary>Through the barrier: gated in or out.</summary>
    Passed,

    /// <summary>Nothing to identify the truck by, or it left.</summary>
    Abandoned,
}

/// <summary>One truck at a lane, as the lane controller handles it.</summary>
public sealed class LaneTransaction
{
    public long TransactionId { get; set; }
    public int TerminalId { get; set; }
    public int LaneId { get; set; }
    public LaneTransactionStatus Status { get; set; } = LaneTransactionStatus.Open;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset LastReadAt { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public int ReadCount { get; set; }
    public string? Plate { get; set; }
    public short? PlateConfidence { get; set; }

    /// <summary>Comma-separated container numbers, normalized.</summary>
    public string? Containers { get; set; }

    public string? Tag { get; set; }

    /// <summary>An appointment number typed at the kiosk.</summary>
    public string? AppointmentRead { get; set; }

    /// <summary>A scanned gate pass, kept until the decision.</summary>
    public string? QrToken { get; set; }

    public decimal? WeightKg { get; set; }
    public long? AppointmentId { get; set; }
    public string? AppointmentNo { get; set; }
    public long? TroubleId { get; set; }

    /// <summary>Why the truck was referred: a trouble reason (NO_APPOINTMENT…) or EXIT.</summary>
    public string? Reason { get; set; }

    public string? Message { get; set; }
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
