namespace Tas.Domain;

public enum TerminalType
{
    Container,
    Multipurpose,
}

/// <summary>A terminal served by this deployment (the tenant).</summary>
public sealed class Terminal
{
    public int TerminalId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public TerminalType TerminalType { get; set; }

    /// <summary>IANA time zone id, e.g. "Asia/Riyadh".</summary>
    public string TimeZone { get; set; } = "Asia/Riyadh";

    /// <summary>Length of one bookable slot: 15, 30, 60 or 120 minutes.</summary>
    public int SlotMinutes { get; set; } = 60;

    /// <summary>How many days ahead slots are generated and bookable.</summary>
    public int BookingHorizonDays { get; set; } = 14;

    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
}
