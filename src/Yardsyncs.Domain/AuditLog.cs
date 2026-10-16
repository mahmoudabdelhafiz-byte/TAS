namespace Yardsyncs.Domain;

public enum AuditAction
{
    Insert,
    Update,
    Delete,
}

/// <summary>Who changed which configuration, when, why, and what it looked like before and after (JSON).</summary>
public sealed class AuditLog
{
    public long AuditId { get; set; }
    public int? TerminalId { get; set; }
    public string EntityName { get; set; } = "";
    public string EntityKey { get; set; } = "";
    public AuditAction Action { get; set; }
    public string? OldValues { get; set; }
    public string? NewValues { get; set; }
    public string? Reason { get; set; }
    public int ChangedBy { get; set; }
    public DateTimeOffset ChangedAt { get; set; }
}
