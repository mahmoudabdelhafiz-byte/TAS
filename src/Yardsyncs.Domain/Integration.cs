namespace Yardsyncs.Domain;

/// <summary>
/// Something the terminal's own systems may want to know (TOS, control room, yard management), kept as an outbox
/// row and delivered to the terminal's webhook when it subscribes to that type.
/// </summary>
public sealed class IntegrationEvent
{
    public long EventId { get; set; }
    public int TerminalId { get; set; }
    public string EventType { get; set; } = "";
    public long? AppointmentId { get; set; }

    /// <summary>The event as JSON (what the webhook receives, plus eventId).</summary>
    public string Payload { get; set; } = "{}";

    /// <summary>QUEUED, SENT, FAILED or SKIPPED (no webhook for this type).</summary>
    public string Status { get; set; } = IntegrationEventStatus.Queued;

    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
}

public static class IntegrationEventStatus
{
    public const string Queued = "QUEUED";
    public const string Sent = "SENT";
    public const string Failed = "FAILED";
    public const string Skipped = "SKIPPED";
}

public static class IntegrationEventTypes
{
    /// <summary>The driver app checked the truck in at the pre-gate.</summary>
    public const string TruckCheckedIn = "TRUCK_CHECKED_IN";

    /// <summary>Inside the terminal, the truck reached the block (or CFS) of one of its stops.</summary>
    public const string TruckAtBlock = "TRUCK_AT_BLOCK";

    /// <summary>The driver confirmed a stop is done ("Done here").</summary>
    public const string StopCompleted = "STOP_COMPLETED";

    /// <summary>A visit was booked: the pre-announcement for a zone system such as Nafith.</summary>
    public const string AppointmentBooked = "APPOINTMENT_BOOKED";

    /// <summary>A visit moved to another hour, or its truck or driver changed.</summary>
    public const string AppointmentChanged = "APPOINTMENT_CHANGED";

    /// <summary>A visit was cancelled.</summary>
    public const string AppointmentCancelled = "APPOINTMENT_CANCELLED";

    public static IReadOnlyList<string> All => [TruckCheckedIn, TruckAtBlock, StopCompleted, AppointmentBooked, AppointmentChanged, AppointmentCancelled];
}
