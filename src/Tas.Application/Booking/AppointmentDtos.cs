using Tas.Domain.Booking;

namespace Tas.Application.Booking;

public sealed record AppointmentDto(
    string AppointmentNo,
    AppointmentStatus Status,
    long SlotId,
    DateTimeOffset SlotStart,
    DateTimeOffset SlotEnd,
    string SlotLocal,
    int CompanyId,
    string CompanyName,
    int? TruckId,
    string? TruckPlate,
    int? DriverId,
    string? DriverName,
    bool IsDual,
    TrafficLight TrafficLight,
    string TrafficReason,
    string QrToken,
    IReadOnlyList<AppointmentItemDto> Items,
    DateTimeOffset CreatedAt);

public sealed record AppointmentItemDto(
    string Category,
    string? ContainerNo,
    string? IsoType,
    string? BookingRef,
    string? EdoRef,
    string? CargoRef,
    decimal? PlannedTonnage,
    ValidationStatus ValidationStatus,
    IReadOnlyList<CheckDto> Checks);

/// <param name="Item">1-based item number in the request, when the check belongs to an item.</param>
public sealed record CheckDto(int? Item, CheckType Type, CheckResult Result, string? Message, string Source);

public enum BookingOutcomeKind
{
    Created,
    Updated,
    Invalid,
    NotFound,
    Rejected,
    NoCapacity,
    Conflict,
}

/// <summary>Result of a booking command; the API maps <see cref="Kind"/> to an HTTP status.</summary>
public sealed record BookingOutcome(
    BookingOutcomeKind Kind,
    AppointmentDto? Appointment,
    IReadOnlyList<string> Errors,
    IReadOnlyList<CheckDto> Checks)
{
    public static BookingOutcome Created(AppointmentDto a) => new(BookingOutcomeKind.Created, a, [], []);
    public static BookingOutcome Updated(AppointmentDto a) => new(BookingOutcomeKind.Updated, a, [], []);
    public static BookingOutcome Invalid(IReadOnlyList<string> errors) => new(BookingOutcomeKind.Invalid, null, errors, []);
    public static BookingOutcome Invalid(string error) => Invalid(new[] { error });
    public static BookingOutcome NotFound(string error) => new(BookingOutcomeKind.NotFound, null, new[] { error }, []);
    public static BookingOutcome NoCapacity(string error) => new(BookingOutcomeKind.NoCapacity, null, new[] { error }, []);
    public static BookingOutcome Conflict(string error) => new(BookingOutcomeKind.Conflict, null, new[] { error }, []);

    public static BookingOutcome Rejected(IReadOnlyList<string> errors, IReadOnlyList<CheckDto> checks) =>
        new(BookingOutcomeKind.Rejected, null, errors, checks);

    public static BookingOutcome Rejected(string error) => Rejected(new[] { error }, []);
}
