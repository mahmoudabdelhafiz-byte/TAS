using Tas.Domain;

namespace Tas.Application.Booking;

/// <summary>A request to book one truck visit into a slot.</summary>
/// <param name="UserId">The portal user making the booking (will come from authentication later).</param>
/// <param name="WalkIn">Booked at the gate for a truck that arrived without an appointment: may use the walk-in reserve.</param>
public sealed record BookingRequest(
    int CompanyId,
    int UserId,
    long SlotId,
    int? TruckId,
    int? DriverId,
    IReadOnlyList<BookingItemRequest> Items,
    bool WalkIn = false);

/// <summary>
/// One move. Container categories need a container number (or, for an empty pickup, just a booking reference).
/// Cargo categories need a cargo reference (BL / delivery order); each cargo item is one truck trip.
/// </summary>
public sealed record BookingItemRequest(
    string Category,
    string? ContainerNo = null,
    string? IsoType = null,
    string? BookingRef = null,
    string? EdoRef = null,
    string? CargoRef = null,
    decimal? PlannedTonnage = null);

/// <summary>A request item after validation: category resolved and container number normalized.</summary>
public sealed record ValidatedItem(
    int Index,
    TransactionCategory Category,
    string? ContainerNo,
    string? IsoType,
    string? BookingRef,
    string? EdoRef,
    string? CargoRef,
    decimal? PlannedTonnage);

public static class BookingRequestValidator
{
    /// <summary>Two 20' moves out and two in is the most one truck can do in a visit.</summary>
    public const int MaxItemsPerAppointment = 4;

    /// <summary>
    /// Checks the shape of the request (not the TOS state). Returns the cleaned items, or the list of problems.
    /// </summary>
    public static (IReadOnlyList<ValidatedItem> Items, IReadOnlyList<string> Errors) Validate(
        BookingRequest request, IReadOnlyDictionary<string, TransactionCategory> activeCategoriesByCode)
    {
        var errors = new List<string>();
        var items = new List<ValidatedItem>();

        if (request.Items is null || request.Items.Count == 0)
            return (Array.Empty<ValidatedItem>(), new[] { "At least one item is required." });

        if (request.Items.Count > MaxItemsPerAppointment)
            errors.Add($"An appointment can hold at most {MaxItemsPerAppointment} items.");

        var seenContainers = new HashSet<string>();

        for (var i = 0; i < request.Items.Count; i++)
        {
            var item = request.Items[i];
            var label = $"Item {i + 1}";

            if (!activeCategoriesByCode.TryGetValue(item.Category?.Trim().ToUpperInvariant() ?? "", out var category))
            {
                errors.Add($"{label}: unknown category '{item.Category}'.");
                continue;
            }

            string? container = null;
            if (!string.IsNullOrWhiteSpace(item.ContainerNo))
            {
                container = ContainerNumber.Normalize(item.ContainerNo);
                if (!ContainerNumber.HasValidFormat(container))
                    errors.Add($"{label}: '{item.ContainerNo}' is not a valid container number (4 letters + 7 digits).");
                else if (!seenContainers.Add(container))
                    errors.Add($"{label}: container {container} appears twice.");
            }

            var cargoRef = string.IsNullOrWhiteSpace(item.CargoRef) ? null : item.CargoRef.Trim().ToUpperInvariant();
            var bookingRef = string.IsNullOrWhiteSpace(item.BookingRef) ? null : item.BookingRef.Trim().ToUpperInvariant();

            if (category.Mode == CategoryMode.Container)
            {
                if (container is null && bookingRef is null)
                    errors.Add($"{label}: {category.Code} needs a container number (or a booking reference for an empty pickup).");
                if (cargoRef is not null)
                    errors.Add($"{label}: {category.Code} is a container category; remove the cargo reference.");
            }
            else
            {
                if (cargoRef is null)
                    errors.Add($"{label}: {category.Code} needs a cargo reference (BL / delivery order).");
                if (container is not null)
                    errors.Add($"{label}: {category.Code} is a cargo category; remove the container number.");
                if (item.PlannedTonnage is <= 0)
                    errors.Add($"{label}: planned tonnage must be positive.");
            }

            items.Add(new ValidatedItem(
                i, category, container, item.IsoType?.Trim().ToUpperInvariant(), bookingRef,
                string.IsNullOrWhiteSpace(item.EdoRef) ? null : item.EdoRef.Trim().ToUpperInvariant(),
                cargoRef, item.PlannedTonnage));
        }

        return errors.Count > 0
            ? (Array.Empty<ValidatedItem>(), errors)
            : (items, Array.Empty<string>());
    }
}
