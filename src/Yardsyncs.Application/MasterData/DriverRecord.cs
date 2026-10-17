using Yardsyncs.Domain;
using Yardsyncs.Domain.Gate;
using Yardsyncs.Domain.Parties;

namespace Yardsyncs.Application.MasterData;

public enum DriverStanding
{
    /// <summary>Under the warning threshold (or warnings are off).</summary>
    Clear,

    /// <summary>At or over the warning threshold: the gate and the control room are told.</summary>
    Warning,

    /// <summary>At or over the limit: the driver goes to the trouble desk at the gate.</summary>
    OverLimit,
}

/// <param name="Counts">The points count now: not withdrawn, not a dispute the terminal accepted, inside the period.</param>
public sealed record IncidentView(
    long IncidentId, int TerminalId, int DriverId, string? AppointmentNo, IncidentKind Kind, int Points, DateTimeOffset OccurredAt,
    string? Note, string? RecordedByName, DateTimeOffset RecordedAt, DisputeStatus? DisputeStatus, string? DisputeText,
    DateTimeOffset? DisputedAt, string? DisputeAnswer, DateTimeOffset? DecidedAt, DateTimeOffset? WithdrawnAt, string? WithdrawReason,
    bool Counts, DateTimeOffset CountsUntil);

/// <summary>A driver's record at a terminal: points that count now, where that stands, and the entries (newest first).</summary>
public sealed record DriverRecordView(
    int DriverId, string FullName, string Mobile, string CompanyName, int Points, DriverStanding Standing,
    int WarnPoints, int LimitPoints, int PointsDays, IReadOnlyList<IncidentView> Incidents);

/// <summary>A driver with points, for the list (most points first).</summary>
public sealed record DriverPointsRow(int DriverId, string FullName, string Mobile, string CompanyName, int Points, DriverStanding Standing, int Open, DateTimeOffset? LastAt);

public sealed record IncidentInput(IncidentKind Kind, int? Points = null, string? AppointmentNo = null, DateTimeOffset? OccurredAt = null, string? Note = null);

public sealed record DisputeInput(string? Text);

public sealed record DisputeDecisionInput(bool Accept, string? Answer);

/// <summary>Points per kind, what counts and where a driver stands against the terminal's thresholds.</summary>
public static class DriverRecordRules
{
    public const int MaxNote = 500;

    /// <summary>Suggested points per kind (staff can give 1–10).</summary>
    public static int DefaultPoints(IncidentKind kind) => kind switch
    {
        IncidentKind.NoShow => 1,
        IncidentKind.NoPpe or IncidentKind.WrongArea or IncidentKind.Other => 2,
        IncidentKind.Speeding => 3,
        IncidentKind.UnsafeDriving => 4,
        IncidentKind.Damage or IncidentKind.Abusive => 5,
        _ => 2,
    };

    /// <summary>Whether an entry's points count at <paramref name="now"/>.</summary>
    public static bool Counts(DateTimeOffset occurredAt, DateTimeOffset? withdrawnAt, DisputeStatus? dispute, int pointsDays, DateTimeOffset now) =>
        withdrawnAt is null && dispute != DisputeStatus.Accepted && occurredAt.AddDays(pointsDays) > now;

    public static DriverStanding Standing(int points, TerminalSettings settings) =>
        settings.DriverLimitPoints > 0 && points >= settings.DriverLimitPoints ? DriverStanding.OverLimit
        : settings.DriverWarnPoints > 0 && points >= settings.DriverWarnPoints ? DriverStanding.Warning
        : DriverStanding.Clear;

    /// <summary>What is wrong with a new entry, or null.</summary>
    public static string? Check(IncidentInput input, DateTimeOffset now)
    {
        if (!Enum.IsDefined(input.Kind))
            return "Choose what happened.";
        if (input.Points is < 1 or > 10)
            return "Points are from 1 to 10.";
        if (input.OccurredAt is { } at && (at > now.AddMinutes(5) || at < now.AddDays(-90)))
            return "When it happened must be in the last 90 days.";
        if (input.Note?.Trim().Length > MaxNote)
            return $"A note is at most {MaxNote} characters.";
        if (input.Kind == IncidentKind.Other && string.IsNullOrWhiteSpace(input.Note))
            return "Say what happened in the note.";
        return null;
    }

    /// <summary>The line the gate and the control room see for a driver with points, or null when clear.</summary>
    public static string? GateWarning(string driverName, int points, DriverStanding standing, TerminalSettings settings) => standing switch
    {
        DriverStanding.OverLimit => $"Driver {driverName} has {points} penalty points at this terminal (limit {settings.DriverLimitPoints}): check with the control room.",
        DriverStanding.Warning => $"Driver {driverName} has {points} penalty points at this terminal.",
        _ => null,
    };
}
