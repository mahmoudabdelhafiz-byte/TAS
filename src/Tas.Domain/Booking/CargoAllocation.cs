namespace Tas.Domain.Booking;

public enum CargoType
{
    Breakbulk,
    Bulk,
    Roro,
    Project,
}

public enum CargoStatus
{
    Open,
    Hold,
    Closed,
}

/// <summary>
/// Multipurpose cargo released for delivery under a BL / cargo reference.
/// Each truck trip booked against it draws down <see cref="BookedTrips"/>.
/// </summary>
public sealed class CargoAllocation
{
    public int CargoAllocationId { get; set; }
    public int TerminalId { get; set; }
    public int? CompanyId { get; set; }
    public string CargoRef { get; set; } = "";
    public string? VesselVisitRef { get; set; }
    public string? Commodity { get; set; }
    public CargoType CargoType { get; set; }
    public decimal? TotalTonnage { get; set; }

    /// <summary>Maximum truck trips; null = not limited by trips.</summary>
    public int? TotalTrips { get; set; }

    public decimal DeliveredTonnage { get; set; }
    public int BookedTrips { get; set; }
    public CargoStatus Status { get; set; } = CargoStatus.Open;
}
