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

    /// <summary>Administers every terminal of one operator (like a TerminalAdmin at each of them).</summary>
    OperatorAdmin,
    SysAdmin,
}

/// <summary>A portal user: company staff (CompanyId set), terminal staff (TerminalId set) or an operator admin (OperatorId set).</summary>
public sealed class AppUser
{
    public int UserId { get; set; }
    public int? CompanyId { get; set; }
    public int? TerminalId { get; set; }
    public int? OperatorId { get; set; }
    public string Email { get; set; } = "";
    public string? Mobile { get; set; }
    public string FullName { get; set; } = "";
    public UserRole Role { get; set; }
    public string PreferredLanguage { get; set; } = "en";
    public bool IsActive { get; set; } = true;

    /// <summary>ASP.NET Core Identity password hash; null until a password is set.</summary>
    public string? PasswordHash { get; set; }

    public int FailedLoginCount { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public bool IsTerminalStaff => Role is UserRole.GateClerk or UserRole.Planner or UserRole.TerminalAdmin or UserRole.OperatorAdmin or UserRole.SysAdmin;
}

/// <summary>A rotating refresh token. Only the SHA-256 hash of the token is stored.</summary>
/// <summary>A container a portal user follows (email when it becomes ready to collect).</summary>
public sealed class ContainerWatch
{
    public long WatchId { get; set; }
    public int TerminalId { get; set; }
    public int UserId { get; set; }
    public string ContainerNo { get; set; } = "";
    public string? LastState { get; set; }
    public string? LastMessage { get; set; }
    public DateTimeOffset? LastCheckedAt { get; set; }
    public DateTimeOffset? ReadyNotifiedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
}

/// <summary>A one-time link to set a password: RESET (forgot password) or INVITE (new user).</summary>
public sealed class PasswordToken
{
    public long TokenId { get; set; }
    public int UserId { get; set; }
    public string Purpose { get; set; } = "RESET";
    public byte[] TokenHash { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
    public int? CreatedBy { get; set; }
}

public sealed class RefreshToken
{
    public long RefreshTokenId { get; set; }
    public int UserId { get; set; }
    public byte[] TokenHash { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public byte[]? ReplacedByHash { get; set; }
    public string? CreatedIp { get; set; }
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
