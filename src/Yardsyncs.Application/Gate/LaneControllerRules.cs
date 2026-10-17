using Yardsyncs.Domain.Gate;

namespace Yardsyncs.Application.Gate;

/// <summary>How the lane controller paces itself (configuration LaneController:*).</summary>
public sealed class LaneControllerOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>How often the controller looks for trucks to decide on.</summary>
    public int IntervalMilliseconds { get; set; } = 1000;

    /// <summary>
    /// Quiet time after the last read before deciding, so the front and rear cameras both report. A kiosk entry or a
    /// scanned gate pass is decided at once. 0 decides on every read.
    /// </summary>
    public double SettleSeconds { get; set; } = 3;

    /// <summary>Reads this close together at a lane belong to the same truck.</summary>
    public double WindowSeconds { get; set; } = 30;

    /// <summary>A transaction with nothing to identify the truck by is given up after this long.</summary>
    public double AbandonSeconds { get; set; } = 120;
}

/// <summary>A command the lane controller sends, in order.</summary>
public sealed record PlannedCommand(LaneCommand Command, string? Text = null);

/// <summary>A transaction as the gate screen and the clerks see it.</summary>
/// <param name="Corrected">A clerk corrected what the cameras read.</param>
public sealed record LaneTransactionView(
    long TransactionId,
    int LaneId,
    string LaneCode,
    LaneTransactionStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset LastReadAt,
    DateTimeOffset? DecidedAt,
    DateTimeOffset? ClosedAt,
    int ReadCount,
    string? Plate,
    IReadOnlyList<string> Containers,
    string? AppointmentNo,
    bool GatePassScanned,
    decimal? WeightKg,
    long? TroubleId,
    string? Reason,
    string? Message,
    bool Corrected = false)
{
    public static LaneTransactionView From(LaneTransaction t, string laneCode) => new(
        t.TransactionId, t.LaneId, laneCode, t.Status, t.StartedAt, t.LastReadAt, t.DecidedAt, t.ClosedAt, t.ReadCount, t.Plate,
        LaneControllerRules.ContainersOf(t), t.AppointmentNo ?? t.AppointmentRead, t.QrToken is not null, t.WeightKg, t.TroubleId, t.Reason, t.Message,
        t.PlateCorrected || t.ContainersCorrected);
}

/// <summary>Let a truck the exit lane held leave, with why.</summary>
public sealed record LetOutInput(string Note);

/// <summary>
/// What the cameras should have read, from the clerk. Leave a field out to keep it; an empty container list clears
/// them. The lane then checks the truck again.
/// </summary>
public sealed record TransactionCorrectionInput(string? Plate = null, IReadOnlyList<string>? Containers = null, string? AppointmentNo = null);

/// <summary>Everything about one truck at a lane, for the remote clerk: its reads with photos, the commands, its trouble case.</summary>
public sealed record LaneTransactionDetail(
    LaneTransactionView Transaction,
    IReadOnlyList<DeviceReadView> Reads,
    IReadOnlyList<LaneCommandView> Commands,
    TroubleView? Trouble,
    IReadOnlyList<LaneDeviceView> Devices);

/// <summary>
/// The lane controller's rules, free of the database: which reads belong to the same truck, how they add up, when the
/// lane has enough to decide, and which commands follow a decision.
/// </summary>
public static class LaneControllerRules
{
    public const int MaxContainers = 4;

    public static IReadOnlyList<string> ContainersOf(LaneTransaction t) =>
        (t.Containers ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();

    /// <summary>
    /// Whether a new read belongs to this transaction: the lane's latest, still going, last read within the window,
    /// and not plainly another truck (a different plate).
    /// </summary>
    public static bool Belongs(LaneTransaction t, NormalizedRead read, DateTimeOffset now, TimeSpan window) =>
        t.Status is not (LaneTransactionStatus.Passed or LaneTransactionStatus.Abandoned)
        && now - t.LastReadAt <= window
        && (t.Plate is null || read.Plate is null || t.Plate == read.Plate);

    /// <summary>Adds a read: the more confident plate wins, containers add up (at most four), the latest kiosk entry counts.</summary>
    public static void Merge(LaneTransaction t, NormalizedRead read, DateTimeOffset now)
    {
        if (read.Plate is not null && (t.Plate is null || (read.PlateConfidence ?? 0) > (t.PlateConfidence ?? 0)))
        {
            t.Plate = read.Plate;
            t.PlateConfidence = (short?)read.PlateConfidence;
        }
        var containers = ContainersOf(t).ToList();
        foreach (var c in read.Containers)
            if (!containers.Contains(c.Number) && containers.Count < MaxContainers)
                containers.Add(c.Number);
        t.Containers = containers.Count == 0 ? null : string.Join(",", containers);
        t.Tag = read.Tag ?? t.Tag;
        t.AppointmentRead = read.AppointmentNo ?? t.AppointmentRead;
        t.QrToken = read.QrToken ?? t.QrToken;
        t.WeightKg = read.WeightKg ?? t.WeightKg;
        t.ReadCount++;
        t.LastReadAt = now;
    }

    /// <summary>Something the gate can find an appointment by.</summary>
    public static bool CanIdentify(LaneTransaction t) =>
        t.Plate is not null || t.Containers is not null || t.AppointmentRead is not null || t.QrToken is not null;

    /// <summary>Decide now: the driver entered something at the kiosk, or the lane has been quiet long enough.</summary>
    public static bool Ready(LaneTransaction t, DateTimeOffset now, TimeSpan settle) =>
        t.Status == LaneTransactionStatus.Open && CanIdentify(t)
        && (t.AppointmentRead is not null || t.QrToken is not null || now - t.LastReadAt >= settle);

    /// <summary>Give up on a transaction that never got anything to identify the truck by.</summary>
    public static bool Abandon(LaneTransaction t, DateTimeOffset now, TimeSpan abandonAfter) =>
        t.Status == LaneTransactionStatus.Open && !CanIdentify(t) && now - t.StartedAt >= abandonAfter;

    /// <summary>
    /// The commands after a decision. Admitted: green, barrier open, a welcome on the display and, at an entry lane,
    /// the gate ticket. Referred: red and the reason on the display. Commands for devices the lane doesn't have are
    /// simply not sent.
    /// </summary>
    public static IReadOnlyList<PlannedCommand> Plan(LaneDirection direction, bool admitted, string? appointmentNo, string? message, string? ticket)
    {
        if (!admitted)
            return [new(LaneCommand.Red), new(LaneCommand.Show, Display.Refer(message))];
        var commands = new List<PlannedCommand>
        {
            new(LaneCommand.Green),
            new(LaneCommand.Open),
            new(LaneCommand.Show, direction == LaneDirection.In ? Display.Welcome(appointmentNo) : Display.Goodbye),
        };
        if (direction == LaneDirection.In && ticket is not null)
            commands.Add(new(LaneCommand.Print, ticket));
        return commands;
    }

    /// <summary>Texts for the lane display and the gate ticket (English; the display shows what it is sent).</summary>
    public static class Display
    {
        public const string Goodbye = "Thank you. Drive safely.";

        public const string TurnedAway = "Not admitted. Please follow the signs to leave the gate area.";

        public static string Welcome(string? appointmentNo) =>
            appointmentNo is null ? "Go ahead." : $"{appointmentNo}: go ahead.";

        public static string Refer(string? message)
        {
            var text = "Please wait: a clerk will help you.";
            return string.IsNullOrWhiteSpace(message) ? text : Clip($"{text} {message.Trim()}", DeviceRules.MaxTextLength);
        }

        public static string Ticket(string terminalName, string laneCode, string? appointmentNo, string? plate, IReadOnlyList<string> containers, string localTime) =>
            Clip(string.Join("\n", new[]
            {
                terminalName,
                $"Gate ticket · lane {laneCode} · {localTime}",
                appointmentNo is null ? null : $"Appointment: {appointmentNo}",
                plate is null ? null : $"Truck: {plate}",
                containers.Count == 0 ? null : $"Containers: {string.Join(", ", containers)}",
            }.Where(l => l is not null)), DeviceRules.MaxTextLength);

        private static string Clip(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
    }
}
