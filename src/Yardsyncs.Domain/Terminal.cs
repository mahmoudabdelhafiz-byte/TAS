namespace Yardsyncs.Domain;

public enum TerminalType
{
    Container,
    Multipurpose,
}

/// <summary>
/// A terminal operator: a group of terminals on one installation with its own administrators (OperatorAdmin), who
/// manage all of its terminals. Competing operators are kept apart by separate installations, not by this grouping.
/// </summary>
public sealed class TerminalOperator
{
    public int OperatorId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// How Yardsyncs looks and is named: the installation's own (OperatorId null) or an operator's, which shows for the
/// operator's terminals and at its own web address. Anything not set comes from the installation, then Yardsyncs.
/// </summary>
public sealed class Branding
{
    public int BrandingId { get; set; }
    public int? OperatorId { get; set; }
    public string? Name { get; set; }

    /// <summary>The main colour, "#0e7c86" (lower case).</summary>
    public string? Color { get; set; }

    /// <summary>The web address the operator's people use (e.g. tas.harbor-one.example); sign-in shows its brand.</summary>
    public string? Domain { get; set; }

    public byte[]? Logo { get; set; }
    public string? LogoType { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
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

    /// <summary>The operator the terminal belongs to; null when it isn't in a group.</summary>
    public int? OperatorId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
