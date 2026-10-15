namespace Tas.Domain.Parties;

/// <summary>The driver app on one phone. One active device per driver; a new sign-in deactivates the old one.</summary>
public sealed class DriverDevice
{
    public long DeviceId { get; set; }
    public int DriverId { get; set; }
    public string InstallId { get; set; } = "";

    /// <summary>ANDROID or IOS.</summary>
    public string Platform { get; set; } = "ANDROID";

    public string? PushToken { get; set; }
    public string? AppVersion { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset RegisteredAt { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
}

/// <summary>How a driver proved who they are (mobile code today; national e-ID later).</summary>
public sealed class DriverVerification
{
    public long VerificationId { get; set; }
    public int DriverId { get; set; }

    /// <summary>MOBILE_OTP, ID_OTP, NAFATH, LIVENESS or CLERK_MANUAL.</summary>
    public string Method { get; set; } = "MOBILE_OTP";

    public string? Provider { get; set; }
    public string? ProviderRef { get; set; }

    /// <summary>PENDING, VERIFIED, FAILED, EXPIRED or REVOKED.</summary>
    public string Status { get; set; } = "VERIFIED";

    public DateTimeOffset? VerifiedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public int? VerifiedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A one-time sign-in code sent by SMS; only its hash is stored.</summary>
public sealed class DriverOtp
{
    public long OtpId { get; set; }
    public string Mobile { get; set; } = "";
    public string InstallId { get; set; } = "";
    public byte[] CodeHash { get; set; } = [];
    public int Attempts { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>The driver's gate pass: the secret behind the rotating QR (stored encrypted).</summary>
public sealed class GatePass
{
    public long GatePassId { get; set; }
    public int DriverId { get; set; }

    /// <summary>Null = valid at every terminal.</summary>
    public int? TerminalId { get; set; }

    public byte[] PassSecret { get; set; } = [];

    /// <summary>ACTIVE, SUSPENDED, REVOKED or EXPIRED.</summary>
    public string Status { get; set; } = "ACTIVE";

    public DateTimeOffset IssuedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public string? RevokedReason { get; set; }
}

/// <summary>An arrival estimate while the truck is on the way (kept only for EN_ROUTE appointments).</summary>
public sealed class AppointmentEta
{
    public long EtaId { get; set; }
    public long AppointmentId { get; set; }
    public DateTimeOffset EtaAt { get; set; }
    public decimal? DistanceKm { get; set; }
    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }

    /// <summary>APP_GPS, TMS_API or MANUAL.</summary>
    public string Source { get; set; } = "APP_GPS";

    public DateTimeOffset RecordedAt { get; set; }
}

/// <summary>The truck entered or left a terminal area (approach, pre-gate, gate…).</summary>
public sealed class GeofenceEvent
{
    public long GeofenceEventId { get; set; }
    public long AppointmentId { get; set; }
    public int DriverId { get; set; }
    public int GeofenceId { get; set; }

    /// <summary>ENTER, EXIT or DWELL.</summary>
    public string EventType { get; set; } = "ENTER";

    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}
