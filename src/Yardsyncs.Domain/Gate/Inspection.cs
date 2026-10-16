namespace Yardsyncs.Domain.Gate;

public enum InspectionStage
{
    In,
    Out,
}

/// <summary>The seal against the one the company declared.</summary>
public enum SealStatus
{
    Ok,

    /// <summary>A different number than declared.</summary>
    Mismatch,

    /// <summary>No seal on the container.</summary>
    Missing,

    Broken,
    NotChecked,
}

public enum InspectionSource
{
    /// <summary>From the lane cameras' reads.</summary>
    Camera,

    Clerk,
}

public enum DamageSide
{
    Front,
    Rear,
    Left,
    Right,
    Top,
    Underside,
    Inside,
    Door,
}

public enum DamageKind
{
    Dent,
    Hole,
    Cut,
    Scratch,
    Rust,
    Broken,
    Missing,
    Wet,
    Other,
}

public enum DamageSeverity
{
    Minor,
    Major,
}

public enum DisputeStatus
{
    Open,
    Accepted,
    Rejected,
}

/// <summary>One container (or the cargo truck itself) at one gate passage: its seal and the damage seen.</summary>
public sealed class GateInspection
{
    public long InspectionId { get; set; }
    public int TerminalId { get; set; }
    public long AppointmentId { get; set; }
    public InspectionStage Stage { get; set; }

    /// <summary>Normalized container number, or "" for cargo / the truck itself.</summary>
    public string ContainerNo { get; set; } = "";

    public string? SealExpected { get; set; }
    public string? SealSeen { get; set; }
    public SealStatus SealStatus { get; set; } = SealStatus.NotChecked;
    public bool DamageFound { get; set; }
    public string? Notes { get; set; }
    public InspectionSource Source { get; set; }
    public DateTimeOffset InspectedAt { get; set; }

    /// <summary>The clerk; null when the lane cameras recorded it.</summary>
    public int? InspectedBy { get; set; }

    public List<InspectionDamage> Damage { get; set; } = [];
}

public sealed class InspectionDamage
{
    public long DamageId { get; set; }
    public long InspectionId { get; set; }
    /// <summary>Null when the camera system did not say.</summary>
    public DamageSide? Side { get; set; }
    public DamageKind Kind { get; set; }
    public DamageSeverity Severity { get; set; }
    public string? Note { get; set; }

    /// <summary>A picture kept by the camera system (URL or file key).</summary>
    public string? PhotoRef { get; set; }

    /// <summary>A photo uploaded with the inspection.</summary>
    public long? PhotoId { get; set; }
}

/// <summary>A photo a clerk took at the gate, stored with the inspection.</summary>
public sealed class InspectionPhoto
{
    public long PhotoId { get; set; }
    public long InspectionId { get; set; }
    public string ContentType { get; set; } = "";
    public byte[] Bytes { get; set; } = [];
    public int SizeBytes { get; set; }
    public DateTimeOffset UploadedAt { get; set; }
    public int? UploadedBy { get; set; }
}

/// <summary>A trucking company disagrees with an inspection; the terminal accepts or rejects it.</summary>
public sealed class InspectionDispute
{
    public long DisputeId { get; set; }
    public long InspectionId { get; set; }
    public int CompanyId { get; set; }
    public string Reason { get; set; } = "";
    public int OpenedBy { get; set; }
    public DateTimeOffset OpenedAt { get; set; }
    public DisputeStatus Status { get; set; } = DisputeStatus.Open;
    public string? ResolutionNote { get; set; }
    public int? ResolvedBy { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
}
