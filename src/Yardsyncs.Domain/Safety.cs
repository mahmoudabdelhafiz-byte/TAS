namespace Yardsyncs.Domain;

/// <summary>A safety notice for drivers for a period, in their languages (texts as JSON, see SafetyRules).</summary>
public sealed class SafetyCampaign
{
    public int CampaignId { get; set; }
    public int TerminalId { get; set; }
    public string Texts { get; set; } = "{}";
    public string? ImageUrl { get; set; }
    public DateOnly StartsOn { get; set; }
    public DateOnly EndsOn { get; set; }

    /// <summary>Only these companies' drivers; null = every driver.</summary>
    public int[]? CompanyIds { get; set; }

    /// <summary>The driver confirms "I have read it"; the gate mentions drivers who haven't.</summary>
    public bool RequireAck { get; set; } = true;

    public int? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
}

/// <summary>A driver (by mobile number: one person) confirmed a campaign.</summary>
public sealed class SafetyAck
{
    public int CampaignId { get; set; }
    public string Mobile { get; set; } = "";
    public int DriverId { get; set; }
    public DateTimeOffset AckedAt { get; set; }
}

/// <summary>The terminal's safety course: pages to read, then questions (JSON, see SafetyRules).</summary>
public sealed class SafetyInduction
{
    public int InductionId { get; set; }
    public int TerminalId { get; set; }
    public string Pages { get; set; } = "[]";
    public string Questions { get; set; } = "[]";
    public int ValidMonths { get; set; } = 12;
    public int Version { get; set; } = 1;
    public int? UpdatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A driver passed the terminal's induction; valid until the date.</summary>
public sealed class InductionPass
{
    public long PassId { get; set; }
    public int TerminalId { get; set; }
    public string Mobile { get; set; } = "";
    public int DriverId { get; set; }
    public int Version { get; set; }
    public DateTimeOffset PassedAt { get; set; }
    public DateTimeOffset ValidUntil { get; set; }
}
