namespace Yardsyncs.Domain;

/// <summary>Someone who gets the terminal's reports by email: daily and / or weekly, in their language.</summary>
public sealed class ReportSubscription
{
    public int SubscriptionId { get; set; }
    public int TerminalId { get; set; }
    public string Email { get; set; } = "";
    public string Language { get; set; } = "en";
    public bool Daily { get; set; } = true;
    public bool Weekly { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public int? CreatedBy { get; set; }

    /// <summary>Terminal-local day the daily report last went out.</summary>
    public DateOnly? LastDailyOn { get; set; }

    public DateOnly? LastWeeklyOn { get; set; }
}
