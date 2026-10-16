using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yardsyncs.Application.Booking;
using Yardsyncs.Application.Booking.Bulk;
using Yardsyncs.Application.MasterData;
using Yardsyncs.Infrastructure.Persistence;

namespace Yardsyncs.Infrastructure.Booking;

public enum BulkVisitStatus
{
    /// <summary>Booked (real run).</summary>
    Booked,

    /// <summary>Every check passed and capacity is there (dry run).</summary>
    WouldBook,

    Failed,
}

/// <param name="Rows">Sheet row numbers of the moves in this visit.</param>
public sealed record BulkVisitResult(
    string Visit,
    IReadOnlyList<int> Rows,
    string Slot,
    BulkVisitStatus Status,
    string? AppointmentNo,
    IReadOnlyList<string> Errors,
    IReadOnlyList<CheckDto> Checks);

/// <param name="FileErrors">Problems with the file itself (columns, size) — nothing was processed if these are set and Visits is 0.</param>
public sealed record BulkResult(
    bool DryRun,
    int Visits,
    int Succeeded,
    int Failed,
    IReadOnlyList<RowError> FileErrors,
    IReadOnlyList<BulkVisitResult> Results);

/// <summary>
/// Books a whole sheet of truck visits for one company.
///
/// Dry run: every visit goes through all booking checks (request, truck, driver, containers, cargo, TOS) and
/// capacity is checked against what is free now, minus what earlier visits in the same file would take.
/// Real run: visits are booked one by one; a visit that fails does not stop the others.
/// </summary>
public sealed class BulkBookingService(YardsyncsDbContext db, BookingService booking, ILogger<BulkBookingService> logger)
{
    public async Task<BulkResult> ImportAsync(
        int terminalId, int companyId, int userId, IReadOnlyList<IReadOnlyList<string>> table, bool dryRun, CancellationToken ct = default)
    {
        var (rows, parseErrors) = BulkSheet.Parse(table);
        if (rows.Count == 0)
            return new BulkResult(dryRun, 0, 0, 0, parseErrors, []);

        var (visits, groupErrors) = BulkSheet.Group(rows);
        var rowNumbers = rows.Select(r => r.RowNumber).ToHashSet();
        var fileErrors = parseErrors.Where(e => !rowNumbers.Contains(e.Row)).ToList();
        var results = new List<BulkVisitResult>();

        // Rows that failed the file checks are reported as failed visits of their own.
        foreach (var bad in parseErrors.Concat(groupErrors).Where(e => rowNumbers.Contains(e.Row)).GroupBy(e => e.Row))
        {
            var row = rows.First(r => r.RowNumber == bad.Key);
            results.Add(new BulkVisitResult(
                row.Visit ?? $"row-{row.RowNumber}", [row.RowNumber], $"{row.Date} {row.Time}".Trim(),
                BulkVisitStatus.Failed, null, bad.Select(e => e.Message).ToList(), []));
        }

        var terminal = await db.Terminals.AsNoTracking().SingleOrDefaultAsync(t => t.TerminalId == terminalId, ct)
                       ?? throw new KeyNotFoundException($"Terminal {terminalId} not found.");
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(terminal.TimeZone);

        // Look up slots, trucks and drivers for the whole file at once.
        var startsByVisit = visits.ToDictionary(v => v, v => SlotStartUtc(v, timeZone));
        var starts = startsByVisit.Values.Where(s => s is not null).Select(s => s!.Value).ToList();
        var slots = new Dictionary<DateTimeOffset, long>();
        if (starts.Count > 0)
        {
            var from = starts.Min();
            var to = starts.Max();
            var found = await db.Slots.AsNoTracking()
                .Where(s => s.TerminalId == terminalId && s.StartAt >= from && s.StartAt <= to)
                .Select(s => new { s.StartAt, s.SlotId })
                .ToListAsync(ct);
            foreach (var s in found)
                slots[s.StartAt] = s.SlotId;
        }

        var plates = visits.Where(v => v.TruckPlate is not null).Select(v => PlateNumber.Normalize(v.TruckPlate)).Distinct().ToList();
        var trucks = await db.Trucks.AsNoTracking()
            .Where(t => t.CompanyId == companyId && plates.Contains(t.PlateNormalized))
            .ToDictionaryAsync(t => t.PlateNormalized, t => (t.TruckId, t.IsBlocked), ct);

        var idNumbers = visits.Where(v => v.DriverId is not null).Select(v => v.DriverId!.Trim().ToUpperInvariant()).Distinct().ToList();
        var drivers = await db.Drivers.AsNoTracking()
            .Where(d => d.CompanyId == companyId && idNumbers.Contains(d.IdNumber))
            .ToDictionaryAsync(d => d.IdNumber, d => d.DriverId, ct);

        // Capacity already promised to earlier visits of this file (dry run only).
        var usedTotal = new Dictionary<long, int>();
        var usedByCategory = new Dictionary<(long Slot, int Category), int>();
        var usedTrips = new Dictionary<int, int>();

        foreach (var visit in visits)
        {
            var label = $"{visit.Date:yyyy-MM-dd} {visit.Time:HH\\:mm}";
            var rowsOfVisit = visit.Rows.Select(r => r.RowNumber).ToList();
            BulkVisitResult Fail(IReadOnlyList<string> errors, IReadOnlyList<CheckDto>? checks = null) =>
                new(visit.Key, rowsOfVisit, label, BulkVisitStatus.Failed, null, errors, checks ?? Array.Empty<CheckDto>());

            var problems = new List<string>();
            long slotId = 0;
            if (startsByVisit[visit] is not { } start || !slots.TryGetValue(start, out slotId))
                problems.Add($"No slot starts at {label} (check the time is a slot start, e.g. 10:00).");

            int? truckId = null;
            if (visit.TruckPlate is not null)
            {
                if (trucks.TryGetValue(PlateNumber.Normalize(visit.TruckPlate), out var truck))
                    truckId = truck.TruckId;
                else
                    problems.Add($"Truck {visit.TruckPlate} is not registered for your company.");
            }

            int? driverId = null;
            if (visit.DriverId is not null)
            {
                if (drivers.TryGetValue(visit.DriverId.Trim().ToUpperInvariant(), out var id))
                    driverId = id;
                else
                    problems.Add($"Driver {visit.DriverId} is not registered for your company.");
            }

            if (problems.Count > 0)
            {
                results.Add(Fail(problems));
                continue;
            }

            var request = new BookingRequest(
                companyId, userId, slotId, truckId, driverId,
                visit.Rows.Select(r => new BookingItemRequest(r.Category!, r.ContainerNo, r.IsoType, r.BookingRef, r.EdoRef, r.CargoRef, r.PlannedTonnage)).ToList());

            db.ChangeTracker.Clear();

            if (!dryRun)
            {
                var outcome = await booking.CreateAsync(terminalId, request, ct);
                results.Add(outcome.Kind == BookingOutcomeKind.Created
                    ? new BulkVisitResult(visit.Key, rowsOfVisit, label, BulkVisitStatus.Booked, outcome.Appointment!.AppointmentNo, [], outcome.Appointment.Items.SelectMany(i => i.Checks).ToList())
                    : Fail(outcome.Errors, outcome.Checks));
                continue;
            }

            var preview = await booking.PreviewAsync(terminalId, request, ct);
            if (preview.Outcome.Kind != BookingOutcomeKind.Valid)
            {
                results.Add(Fail(preview.Outcome.Errors, preview.Outcome.Checks));
                continue;
            }

            var capacityProblems = new List<string>();
            if (usedTotal.GetValueOrDefault(slotId) + 1 > preview.FreeTotal)
                capacityProblems.Add($"No capacity left at {label} (counting earlier rows of this file).");
            foreach (var (categoryId, units) in preview.UnitsByCategory)
            {
                if (usedByCategory.GetValueOrDefault((slotId, categoryId)) + units > preview.FreeByCategory.GetValueOrDefault(categoryId))
                {
                    var code = visit.Rows.Select(r => r.Category!.Trim().ToUpperInvariant()).Distinct();
                    capacityProblems.Add($"No {string.Join("/", code)} capacity left at {label} (counting earlier rows of this file).");
                    break;
                }
            }
            foreach (var (cargoId, trips) in preview.CargoTrips)
            {
                if (preview.CargoTripsLeft.GetValueOrDefault(cargoId) is { } left && usedTrips.GetValueOrDefault(cargoId) + trips > left)
                    capacityProblems.Add("No trips left on this cargo release (counting earlier rows of this file).");
            }

            if (capacityProblems.Count > 0)
            {
                results.Add(Fail(capacityProblems, preview.Outcome.Checks));
                continue;
            }

            usedTotal[slotId] = usedTotal.GetValueOrDefault(slotId) + 1;
            foreach (var (categoryId, units) in preview.UnitsByCategory)
                usedByCategory[(slotId, categoryId)] = usedByCategory.GetValueOrDefault((slotId, categoryId)) + units;
            foreach (var (cargoId, trips) in preview.CargoTrips)
                usedTrips[cargoId] = usedTrips.GetValueOrDefault(cargoId) + trips;

            results.Add(new BulkVisitResult(visit.Key, rowsOfVisit, label, BulkVisitStatus.WouldBook, null, [], preview.Outcome.Checks));
        }

        var ordered = results.OrderBy(r => r.Rows.Min()).ToList();
        var succeeded = ordered.Count(r => r.Status != BulkVisitStatus.Failed);

        logger.LogInformation("Bulk {Mode} for company {CompanyId} at terminal {TerminalId}: {Ok} of {Total} visits ok",
            dryRun ? "preview" : "booking", companyId, terminalId, succeeded, ordered.Count);

        return new BulkResult(dryRun, ordered.Count, succeeded, ordered.Count - succeeded, fileErrors, ordered);
    }

    private static DateTimeOffset? SlotStartUtc(BulkVisit visit, TimeZoneInfo timeZone)
    {
        var local = visit.Date.ToDateTime(visit.Time, DateTimeKind.Unspecified);
        return timeZone.IsInvalidTime(local)
            ? null
            : new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, timeZone), TimeSpan.Zero);
    }
}
