namespace Yardsyncs.Domain.Booking;

public enum MessageDirection
{
    /// <summary>From the control room to the driver.</summary>
    ToDriver,

    /// <summary>From the driver (the app) to the control room.</summary>
    FromDriver,
}

public enum MessageType
{
    Text,
    Voice,

    /// <summary>A ready-made message (e.g. GO_TO_TROUBLE_DESK), shown in the reader's language.</summary>
    Template,

    /// <summary>The driver asks to be called.</summary>
    CallRequest,
    Location,
}

/// <summary>A message between the driver and the control room about one visit (table appointment_message).</summary>
public sealed class AppointmentMessage
{
    public long MessageId { get; set; }
    public long? AppointmentId { get; set; }
    public int DriverId { get; set; }
    public MessageDirection Direction { get; set; }

    /// <summary>The control room user, for messages to the driver.</summary>
    public int? SenderUserId { get; set; }

    public MessageType MessageType { get; set; }
    public string? TemplateCode { get; set; }

    /// <summary>The text as written; for a ready-made message, its English text (for the record).</summary>
    public string? Body { get; set; }

    public string? MediaUrl { get; set; }
    public DateTimeOffset SentAt { get; set; }

    /// <summary>When the other side opened it.</summary>
    public DateTimeOffset? ReadAt { get; set; }
}
