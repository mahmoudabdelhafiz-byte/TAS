namespace Tas.Domain.Parties;

public enum CompanyType
{
    Trucking,
    Forwarder,
    Consignee,
    Line,
    Other,
}

public enum CompanyStatus
{
    Pending,
    Active,
    Suspended,
}

/// <summary>A trucking company, forwarder or consignee that books appointments.</summary>
public sealed class Company
{
    public int CompanyId { get; set; }
    public string Name { get; set; } = "";
    public string? CrNumber { get; set; }
    public CompanyType CompanyType { get; set; } = CompanyType.Trucking;
    public bool IsPriority { get; set; }
    public CompanyStatus Status { get; set; } = CompanyStatus.Active;
    public int NoShowCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public enum UserRole
{
    CompanyAdmin,
    CompanyUser,
    GateClerk,
    Planner,
    TerminalAdmin,
    SysAdmin,
}

/// <summary>A portal user: company staff (CompanyId set) or terminal staff (TerminalId set).</summary>
public sealed class AppUser
{
    public int UserId { get; set; }
    public int? CompanyId { get; set; }
    public int? TerminalId { get; set; }
    public string Email { get; set; } = "";
    public string? Mobile { get; set; }
    public string FullName { get; set; } = "";
    public UserRole Role { get; set; }
    public string PreferredLanguage { get; set; } = "en";
    public bool IsActive { get; set; } = true;

    public bool IsTerminalStaff => Role is UserRole.GateClerk or UserRole.Planner or UserRole.TerminalAdmin or UserRole.SysAdmin;
}

public sealed class Truck
{
    public int TruckId { get; set; }
    public int CompanyId { get; set; }
    public string PlateNumber { get; set; } = "";

    /// <summary>Upper-case Latin without spaces; used to match gate OCR reads.</summary>
    public string PlateNormalized { get; set; } = "";

    public bool IsBlocked { get; set; }
}

public sealed class Driver
{
    public int DriverId { get; set; }
    public int CompanyId { get; set; }
    public string FullName { get; set; } = "";
    public string IdNumber { get; set; } = "";
    public string Mobile { get; set; } = "";
    public string PreferredLanguage { get; set; } = "ar";
    public bool IsBlocked { get; set; }
}
