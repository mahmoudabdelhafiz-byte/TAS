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

    /// <summary>The terminal whose message providers and sender names are used; null = the installation's.</summary>
    public int? TerminalId { get; set; }

    public NotificationChannel Channel { get; set; }

    /// <summary>e.g. STATUS_RED, STATUS_CLEARED, SLOT_REMINDER.</summary>
    public string TemplateCode { get; set; } = "";

    public string Language { get; set; } = "ar";
    public NotificationStatus Status { get; set; } = NotificationStatus.Queued;
    public string? ProviderRef { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }

    /// <summary>Template values as JSON, e.g. {"appointmentNo": "...", "slot": "...", "reason": "..."}.</summary>
    public string? Params { get; set; }

    /// <summary>Phone number or email address the message was sent to.</summary>
    public string? Recipient { get; set; }

    public int Attempts { get; set; }
    public string? LastError { get; set; }

    /// <summary>When to try (again); null = as soon as possible.</summary>
    public DateTimeOffset? NextAttemptAt { get; set; }
}

public static class NotificationTemplates
{
    /// <summary>A check failed after booking (hold, release withdrawn, unpaid…): don't set off.</summary>
    public const string StatusRed = "STATUS_RED";

    /// <summary>A previously failed check has cleared: good to go.</summary>
    public const string StatusCleared = "STATUS_CLEARED";

    /// <summary>Reminder shortly before the slot, with the time the gate opens for the truck.</summary>
    public const string SlotReminder = "SLOT_REMINDER";

    /// <summary>The terminal closed the slot (e.g. crane breakdown): please reschedule.</summary>
    public const string SlotClosed = "SLOT_CLOSED";

    /// <summary>The truck passed the gate: the in-gate ticket with the block(s) to drive to.</summary>
    public const string GateIn = "GATE_IN";

    /// <summary>A followed container became ready to collect (to the portal user who follows it).</summary>
    public const string ContainerReady = "CONTAINER_READY";
}
