using Tas.Domain.Capacity;
using Tas.Domain.Parties;

namespace Tas.Domain.Booking;

public enum AppointmentStatus
{
    Draft,
    Confirmed,
    EnRoute,
    Arrived,
    GateIn,
    Completed,
    Cancelled,
    NoShow,
    RejectedAtGate,
}

public enum TrafficLight
{
    Grey,
    Green,
    Amber,
    Red,
}

public enum ValidationStatus
{
    Pending,
    Pass,
    Warn,
    Fail,
}

public enum CheckResult
{
    Pass,
    Warn,
    Fail,
}

public enum CheckType
{
    ContainerNumber,
    Availability,
    CustomsRelease,
    LineRelease,
    TerminalHold,
    Payment,
    BookingValid,
    Vgm,
    Cutoff,
    CargoRelease,
}

/// <summary>One truck visit to the terminal, holding one or more moves.</summary>
public sealed class Appointment
{
    public long AppointmentId { get; set; }
    public string AppointmentNo { get; set; } = "";
    public int TerminalId { get; set; }
    public long SlotId { get; set; }
    public Slot Slot { get; set; } = null!;
    public int CompanyId { get; set; }
    public Company Company { get; set; } = null!;
    public int? TruckId { get; set; }
    public Truck? Truck { get; set; }
    public int? DriverId { get; set; }
    public Driver? Driver { get; set; }
    public AppointmentStatus Status { get; set; } = AppointmentStatus.Confirmed;
    public bool IsDual { get; set; }
    public string QrToken { get; set; } = "";
    public TrafficLight TrafficLight { get; set; } = TrafficLight.Grey;
    public string? TrafficReason { get; set; }
    public DateTimeOffset? CurrentEta { get; set; }
    public DateTimeOffset? EtaUpdatedAt { get; set; }
    public string? CheckInMethod { get; set; }

    /// <summary>Last time the items were (re-)checked against the TOS.</summary>
    public DateTimeOffset? LastValidatedAt { get; set; }
    public DateTimeOffset? ArrivedAt { get; set; }
    public DateTimeOffset? GateInAt { get; set; }
    public DateTimeOffset? GateOutAt { get; set; }
    public int CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public List<AppointmentItem> Items { get; set; } = [];
    public List<AppointmentStatusHistory> History { get; set; } = [];

    /// <summary>Statuses in which the appointment still holds capacity and can be cancelled or moved.</summary>
    public static bool IsBeforeGate(AppointmentStatus status) =>
        status is AppointmentStatus.Confirmed or AppointmentStatus.EnRoute or AppointmentStatus.Arrived;

    /// <summary>Statuses in which the appointment is live (its containers cannot be booked again).</summary>
    public static bool IsLive(AppointmentStatus status) =>
        IsBeforeGate(status) || status == AppointmentStatus.GateIn;

    /// <summary>Units consumed per category: one per move (trip for cargo).</summary>
    public IReadOnlyDictionary<int, int> UnitsByCategory() =>
        Items.GroupBy(i => i.CategoryId).ToDictionary(g => g.Key, g => g.Sum(i => i.Trips ?? 1));
}

/// <summary>One move inside an appointment: a container, or a cargo trip for multipurpose cargo.</summary>
public sealed class AppointmentItem
{
    public long ItemId { get; set; }
    public long AppointmentId { get; set; }
    public int CategoryId { get; set; }
    public TransactionCategory Category { get; set; } = null!;
    public string? ContainerNo { get; set; }
    public string? IsoType { get; set; }
    public string? BookingRef { get; set; }
    public string? EdoRef { get; set; }
    public int? CargoAllocationId { get; set; }
    public CargoAllocation? CargoAllocation { get; set; }
    public int? Trips { get; set; }
    public decimal? PlannedTonnage { get; set; }
    public decimal? ActualTonnage { get; set; }
    public ValidationStatus ValidationStatus { get; set; } = ValidationStatus.Pending;
    public string? TosRef { get; set; }
    public List<ValidationResult> ValidationResults { get; set; } = [];
}

/// <summary>The outcome of one check (TOS, customs or internal) on one item.</summary>
public sealed class ValidationResult
{
    public long ValidationId { get; set; }
    public long ItemId { get; set; }
    public CheckType CheckType { get; set; }
    public CheckResult Result { get; set; }
    public string? Message { get; set; }
    public string Source { get; set; } = "";
    public DateTimeOffset CheckedAt { get; set; }
}

public sealed class AppointmentStatusHistory
{
    public long HistoryId { get; set; }
    public long AppointmentId { get; set; }
    public AppointmentStatus? FromStatus { get; set; }
    public AppointmentStatus ToStatus { get; set; }
    public int? ChangedBy { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset ChangedAt { get; set; }
}
