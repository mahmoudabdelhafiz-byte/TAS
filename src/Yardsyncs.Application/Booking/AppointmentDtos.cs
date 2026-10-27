using Yardsyncs.Domain.Booking;

namespace Yardsyncs.Application.Booking;

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
    DateTimeOffset CreatedAt,
    DateTimeOffset? Eta = null);

public sealed record AppointmentItemDto(
    string Category,
    string? ContainerNo,
    string? IsoType,
    string? BookingRef,
    string? EdoRef,
    string? CargoRef,
    decimal? PlannedTonnage,
    ValidationStatus ValidationStatus,
    IReadOnlyList<CheckDto> Checks,
    string? SealNo = null,
    string? Vin = null,
    string? VehicleDesc = null);

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

    /// <summary>Preview only: every check passed; the booking would go through if capacity allows.</summary>
    Valid,
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
    public static BookingOutcome Valid(IReadOnlyList<CheckDto> checks) => new(BookingOutcomeKind.Valid, null, [], checks);
}

/// <summary>
/// Result of checking a booking without making it: the outcome (Valid or the reason it would fail),
/// what it would consume, and what is still free.
/// </summary>
/// <param name="FreeByCategory">Units still bookable per category in the slot for this company.</param>
/// <param name="CargoTrips">Trips the booking needs per cargo allocation.</param>
/// <param name="CargoTripsLeft">Trips left per cargo allocation (null = unlimited).</param>
public sealed record BookingPreview(
    BookingOutcome Outcome,
    long? SlotId,
    IReadOnlyDictionary<int, int> UnitsByCategory,
    int FreeTotal,
    IReadOnlyDictionary<int, int> FreeByCategory,
    IReadOnlyDictionary<int, int> CargoTrips,
    IReadOnlyDictionary<int, int?> CargoTripsLeft);
