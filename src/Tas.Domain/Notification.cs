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

    /// <summary>Not sent because it no longer applies (appointment cancelled, slot passed, superseded…).</summary>
    Skipped,
}

/// <summary>
/// Outbound notification. Rows are written as Queued (an outbox); the sender picks them up, renders the
/// template in the recipient's language and marks them Sent, Failed or Skipped. Transient delivery errors
/// keep the row Queued with a later <see cref="NextAttemptAt"/>.
/// </summary>
public sealed class NotificationLog
{
    public long NotificationId { get; set; }
    public long? AppointmentId { get; set; }
    public int? DriverId { get; set; }
    public int? UserId { get; set; }
    public NotificationChannel Channel { get; set; }

    /// <summary>e.g. STATUS_RED, STATUS_CLEARED, SLOT_REMINDER, SLOT_CLOSED.</summary>
    public string TemplateCode { get; set; } = "";

    public string Language { get; set; } = "ar";
    public NotificationStatus Status { get; set; } = NotificationStatus.Queued;

    /// <summary>Mobile number or email address the message went to (filled when sending).</summary>
    public string? Recipient { get; set; }

    public int Attempts { get; set; }

    /// <summary>Not before this time (retry back-off, or the lease of a sender working on it). Null = now.</summary>
    public DateTimeOffset? NextAttemptAt { get; set; }

    public string? LastError { get; set; }

    /// <summary>
    /// Encrypted data the message needs but the database must not keep in clear text (e.g. a password link).
    /// Cleared once the message is sent, failed or skipped.
    /// </summary>
    public string? Payload { get; set; }

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

    /// <summary>Sent a few hours before the slot.</summary>
    public const string SlotReminder = "SLOT_REMINDER";

    /// <summary>The terminal closed the slot of a booked appointment; it needs a new slot.</summary>
    public const string SlotClosed = "SLOT_CLOSED";

    /// <summary>Forgotten-password link (email to the user).</summary>
    public const string PasswordReset = "PASSWORD_RESET";

    /// <summary>Invitation for a new user to set their password (email to the user).</summary>
    public const string AccountInvite = "ACCOUNT_INVITE";

    /// <summary>Templates about an appointment.</summary>
    public static readonly IReadOnlyList<string> All = [StatusRed, StatusCleared, SlotReminder, SlotClosed];

    /// <summary>Templates about the user's account; not linked to an appointment.</summary>
    public static readonly IReadOnlyList<string> Account = [PasswordReset, AccountInvite];

    public static bool IsAccount(string template) => template is PasswordReset or AccountInvite;
}
