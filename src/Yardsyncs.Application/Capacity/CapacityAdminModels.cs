using Yardsyncs.Domain.Capacity;

namespace Yardsyncs.Application.Capacity;

// ---------------------------------------------------------------- inputs (what the planner sends)

/// <param name="Start">Local time "HH:mm".</param>
/// <param name="End">Local time "HH:mm"; "24:00" or "23:59" = until midnight.</param>
/// <param name="Quotas">Max units per category code, e.g. { "IMP_PICKUP": 15 }. Categories left out are limited only by the total.</param>
public sealed record TemplateWindowInput(
    DayOfWeek Day,
    string Start,
    string End,
    int TotalCapacity,
    decimal OverbookPct = 0,
    IReadOnlyDictionary<string, int>? Quotas = null);

/// <summary>A whole template. Saving replaces all of its windows (the planner's grid is saved as one).</summary>
public sealed record TemplateInput(
    string Name,
    bool IsDefault,
    DateOnly? ValidFrom,
    DateOnly? ValidTo,
    int Priority,
    IReadOnlyList<TemplateWindowInput> Windows,
    string? Reason = null);

/// <param name="Category">Category code, or null for the slot total.</param>
public sealed record OverrideInput(
    DateTimeOffset StartAt,
    DateTimeOffset EndAt,
    string? Category,
    OverrideAction Action,
    int? Value,
    string Reason);

/// <param name="CompanyId">Null = walk-in / emergency reserve.</param>
public sealed record ReservationInput(
    int? CompanyId,
    string? Category,
    DateTimeOffset StartAt,
    DateTimeOffset EndAt,
    int UnitsPerSlot,
    int ReleaseHoursBefore,
    string? Reason = null);

public sealed record ReleaseRuleInput(string Category, int HoursBefore);

public sealed record ReasonInput(string? Reason);

// ---------------------------------------------------------------- views (what the planner sees; also stored in the audit log)

public sealed record TemplateWindowView(
    DayOfWeek Day,
    string Start,
    string End,
    int TotalCapacity,
    decimal OverbookPct,
    IReadOnlyDictionary<string, int> Quotas);

public sealed record TemplateView(
    int TemplateId,
    string Name,
    bool IsDefault,
    DateOnly? ValidFrom,
    DateOnly? ValidTo,
    int Priority,
    bool IsActive,
    IReadOnlyList<TemplateWindowView> Windows);

public sealed record OverrideView(
    int OverrideId,
    DateTimeOffset StartAt,
    DateTimeOffset EndAt,
    string? Category,
    OverrideAction Action,
    int? Value,
    string Reason,
    bool IsActive,
    int CreatedBy,
    DateTimeOffset CreatedAt);

public sealed record ReservationView(
    int ReservationId,
    int? CompanyId,
    string? Category,
    DateTimeOffset StartAt,
    DateTimeOffset EndAt,
    int UnitsPerSlot,
    int ReleaseHoursBefore,
    bool IsActive);

public sealed record ReleaseRuleView(string Category, int HoursBefore);

public sealed record SlotBoardCategory(string Category, int Quota, int Booked);

/// <summary>One row of the planner's slot board: capacity vs booked for a slot.</summary>
public sealed record SlotBoardRow(
    long SlotId,
    DateTimeOffset StartAt,
    DateTimeOffset EndAt,
    string Local,
    int TotalCapacity,
    int TotalBooked,
    bool IsClosed,
    bool OverCapacity,
    IReadOnlyList<SlotBoardCategory> Categories);

public sealed record AuditEntryView(
    long AuditId,
    string Entity,
    string Key,
    string Action,
    string? OldValues,
    string? NewValues,
    string? Reason,
    int ChangedBy,
    DateTimeOffset ChangedAt);

public enum AdminStatus
{
    Ok,
    Created,
    Invalid,
    NotFound,
    Conflict,
    Forbidden,
}

/// <summary>Result of a configuration change, with the regenerated-slot summary when slots were affected.</summary>
public sealed record AdminResult<T>(AdminStatus Status, T? Value, IReadOnlyList<string> Errors, object? Regenerated = null)
{
    public static AdminResult<T> Ok(T value, object? regenerated = null) => new(AdminStatus.Ok, value, [], regenerated);
    public static AdminResult<T> Created(T value, object? regenerated = null) => new(AdminStatus.Created, value, [], regenerated);
    public static AdminResult<T> Invalid(IReadOnlyList<string> errors) => new(AdminStatus.Invalid, default, errors);
    public static AdminResult<T> Invalid(string error) => Invalid(new[] { error });
    public static AdminResult<T> NotFound(string error) => new(AdminStatus.NotFound, default, new[] { error });
    public static AdminResult<T> Conflict(string error) => new(AdminStatus.Conflict, default, new[] { error });
    public static AdminResult<T> Forbidden(string error) => new(AdminStatus.Forbidden, default, new[] { error });
}
