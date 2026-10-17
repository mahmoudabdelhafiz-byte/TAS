using Yardsyncs.Domain.Gate;

namespace Yardsyncs.Domain.Parties;

public enum IncidentKind
{
    Speeding,
    UnsafeDriving,

    /// <summary>No personal protective equipment (vest, helmet, shoes) outside the cab.</summary>
    NoPpe,

    /// <summary>In an area the driver was not sent to.</summary>
    WrongArea,
    Damage,
    Abusive,
    NoShow,
    Other,
}

/// <summary>A penalty or misbehaviour recorded against a driver at a terminal, with points (table driver_incident).</summary>
public sealed class DriverIncident
{
    public long IncidentId { get; set; }
    public int TerminalId { get; set; }
    public int DriverId { get; set; }
    public long? AppointmentId { get; set; }
    public IncidentKind Kind { get; set; }
    public short Points { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string? Note { get; set; }
    public int? RecordedBy { get; set; }
    public DateTimeOffset RecordedAt { get; set; }

    /// <summary>The driver disputed it (Open), and the terminal's decision: Accepted withdraws the points.</summary>
    public DisputeStatus? DisputeStatus { get; set; }
    public string? DisputeText { get; set; }
    public DateTimeOffset? DisputedAt { get; set; }
    public string? DisputeAnswer { get; set; }
    public int? DecidedBy { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }

    public DateTimeOffset? WithdrawnAt { get; set; }
    public int? WithdrawnBy { get; set; }
    public string? WithdrawReason { get; set; }
}
