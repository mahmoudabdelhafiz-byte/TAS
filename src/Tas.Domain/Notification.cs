namespace Tas.Domain;

public enum NotificationChannel
{
    Push,
    Sms,
    Whatsapp,
    Email,
}

public enum NotificationStatus
{
    Queued,
    Sent,
    Delivered,
    Failed,
}

/// <summary>
/// Outbound notification. Rows are written as Queued (an outbox); a sender picks them up, renders the
/// template in the recipient's language and marks them Sent / Failed.
/// </summary>
public sealed class NotificationLog
{
    public long NotificationId { get; set; }
    public long? AppointmentId { get; set; }
    public int? DriverId { get; set; }
    public int? UserId { get; set; }
    public NotificationChannel Channel { get; set; }

    /// <summary>e.g. STATUS_RED, STATUS_CLEARED, SLOT_REMINDER.</summary>
    public string TemplateCode { get; set; } = "";

    public string Language { get; set; } = "ar";
    public NotificationStatus Status { get; set; } = NotificationStatus.Queued;
    public string? ProviderRef { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
}

public static class NotificationTemplates
{
    /// <summary>A check failed after booking (hold, release withdrawn, unpaid…): don't set off.</summary>
    public const string StatusRed = "STATUS_RED";

    /// <summary>A previously failed check has cleared: good to go.</summary>
    public const string StatusCleared = "STATUS_CLEARED";
}
