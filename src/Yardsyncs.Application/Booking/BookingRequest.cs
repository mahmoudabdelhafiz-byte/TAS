using Yardsyncs.Domain;

namespace Yardsyncs.Application.Booking;

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
/// Vehicle categories (RoRo) need the VIN; the BL (CargoRef) and make / model are optional.
/// </summary>
public sealed record BookingItemRequest(
    string Category,
    string? ContainerNo = null,
    string? IsoType = null,
    string? BookingRef = null,
    string? EdoRef = null,
    string? CargoRef = null,
    decimal? PlannedTonnage = null,
    string? Vin = null,
    string? VehicleDesc = null);

/// <summary>A request item after validation: category resolved and container number normalized.</summary>
public sealed record ValidatedItem(
    int Index,
    TransactionCategory Category,
    string? ContainerNo,
    string? IsoType,
    string? BookingRef,
    string? EdoRef,
    string? CargoRef,
    decimal? PlannedTonnage,
    string? Vin = null,
    string? VehicleDesc = null);

public static class BookingRequestValidator
{
    /// <summary>Two 20' moves out and two in is the most one truck can do in a visit.</summary>
    public const int MaxItemsPerAppointment = 4;

    /// <summary>Vehicles one car carrier takes when the terminal sets nothing else (setting roro_max_per_truck).</summary>
    public const int DefaultMaxVehiclesPerTruck = 8;

    /// <summary>
    /// Checks the shape of the request (not the TOS state). Returns the cleaned items, or the list of problems.
    /// </summary>
    /// <param name="maxVehicles">Vehicles one truck may take (the terminal's roro_max_per_truck).</param>
    public static (IReadOnlyList<ValidatedItem> Items, IReadOnlyList<string> Errors) Validate(
        BookingRequest request, IReadOnlyDictionary<string, TransactionCategory> activeCategoriesByCode,
        int maxVehicles = DefaultMaxVehiclesPerTruck)
    {
        var errors = new List<string>();
        var items = new List<ValidatedItem>();

        if (request.Items is null || request.Items.Count == 0)
            return (Array.Empty<ValidatedItem>(), new[] { "At least one item is required." });

        // RoRo: a visit moves vehicles only (a car carrier takes several; drive-away is one vehicle and no truck).
        var categories = request.Items
            .Select(i => activeCategoriesByCode.GetValueOrDefault(i.Category?.Trim().ToUpperInvariant() ?? ""))
            .ToList();
        var vehicles = categories.Count(c => c?.Mode == CategoryMode.Vehicle);
        if (vehicles > 0 && vehicles < request.Items.Count)
            errors.Add("A visit moves either vehicles or containers / cargo, not both.");
        else if (vehicles > 0 && categories.Any(c => c!.DriveAway))
        {
            if (request.Items.Count > 1)
                errors.Add("A drive-away visit is one vehicle, driven by its driver.");
            if (request.TruckId is not null)
                errors.Add("A drive-away visit has no truck: the driver drives the vehicle.");
        }
        else if (vehicles > 0 && request.Items.Count > maxVehicles)
            errors.Add($"One truck takes at most {maxVehicles} vehicles.");
        if (vehicles == 0 && request.Items.Count > MaxItemsPerAppointment)
            errors.Add($"An appointment can hold at most {MaxItemsPerAppointment} items.");

        var seenContainers = new HashSet<string>();
        var seenVins = new HashSet<string>();

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

            string? vin = null;
            if (!string.IsNullOrWhiteSpace(item.Vin))
            {
                vin = Vin.Normalize(item.Vin);
                if (!Vin.HasValidFormat(vin))
                    errors.Add($"{label}: '{item.Vin}' is not a valid VIN (17 letters and digits, no I, O or Q).");
                else if (!seenVins.Add(vin))
                    errors.Add($"{label}: vehicle {vin} appears twice.");
            }

            if (category.Mode == CategoryMode.Vehicle)
            {
                if (vin is null)
                    errors.Add($"{label}: {category.Code} needs the vehicle's VIN (chassis number).");
                if (container is not null)
                    errors.Add($"{label}: {category.Code} is a vehicle category; remove the container number.");
            }
            else if (vin is not null)
                errors.Add($"{label}: {category.Code} is not a vehicle category; remove the VIN.");

            if (category.Mode == CategoryMode.Container)
            {
                if (container is null && bookingRef is null)
                    errors.Add($"{label}: {category.Code} needs a container number (or a booking reference for an empty pickup).");
                if (cargoRef is not null)
                    errors.Add($"{label}: {category.Code} is a container category; remove the cargo reference.");
            }
            else if (category.Mode == CategoryMode.Cargo)
            {
                if (cargoRef is null)
                    errors.Add($"{label}: {category.Code} needs a cargo reference (BL / delivery order).");
                if (container is not null)
                    errors.Add($"{label}: {category.Code} is a cargo category; remove the container number.");
                if (item.PlannedTonnage is <= 0)
                    errors.Add($"{label}: planned tonnage must be positive.");
            }

            var edoRef = string.IsNullOrWhiteSpace(item.EdoRef) ? null : item.EdoRef.Trim().ToUpperInvariant();
            if (category.Mode == CategoryMode.Vehicle)
            {
                // A vehicle's BL / delivery order is its release reference, not a cargo allocation drawn down by trips.
                edoRef ??= cargoRef;
                cargoRef = null;
            }

            items.Add(new ValidatedItem(
                i, category, container, item.IsoType?.Trim().ToUpperInvariant(), bookingRef,
                edoRef,
                cargoRef, category.Mode == CategoryMode.Vehicle ? null : item.PlannedTonnage, vin,
                string.IsNullOrWhiteSpace(item.VehicleDesc) ? null : item.VehicleDesc.Trim()[..Math.Min(60, item.VehicleDesc.Trim().Length)]));
        }

        return errors.Count > 0
            ? (Array.Empty<ValidatedItem>(), errors)
            : (items, Array.Empty<string>());
    }
}
