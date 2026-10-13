using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Tas.Application.Booking;
using Tas.Application.Capacity;
using Tas.Application.Tos;
using Tas.Domain;
using Tas.Domain.Booking;
using Tas.Domain.Capacity;
using Tas.Domain.Parties;
using Tas.Infrastructure.Capacity;
using Tas.Infrastructure.Persistence;

namespace Tas.Infrastructure.Booking;

/// <summary>
/// Books, cancels, moves and assigns appointments.
///
/// Booking order: check the request → check company/truck/driver/containers → validate against the TOS
/// (outside the transaction, it may be slow) → in one transaction consume slot and cargo capacity
/// with race-safe counters and insert the appointment. A failed TOS check rejects the booking, so
/// trucks never set off for a container they cannot collect.
/// </summary>
public sealed class BookingService(
    TasDbContext db,
    BookingCounterStore counters,
    ITosAdapter tos,
    AppointmentQueryService queries,
    TimeProvider clock,
    ILogger<BookingService> logger)
{
    private const string InternalSource = "INTERNAL";

    // ------------------------------------------------------------------ create

    /// <summary>Everything a booking needs, gathered and checked before any capacity is used.</summary>
    private sealed record Prepared(
        Terminal Terminal,
        Company Company,
        AppUser User,
        Slot Slot,
        IReadOnlyList<ValidatedItem> Items,
        IReadOnlyDictionary<string, CargoAllocation> Cargo,
        IReadOnlyList<(int Index, CheckType Type, CheckResult Result, string? Message, string Source, string? TosRef)> Checks,
        IReadOnlyList<CheckDto> CheckDtos,
        TerminalSettings Settings,
        TimeZoneInfo TimeZone);

    public async Task<BookingOutcome> CreateAsync(int terminalId, BookingRequest request, CancellationToken ct = default)
    {
        var (prepared, error) = await PrepareAsync(terminalId, request, ct);
        return error ?? await CommitAsync(prepared!, request, ct);
    }

    /// <summary>
    /// Runs every check CreateAsync runs (request, company, truck, driver, containers, cargo, TOS) and reports
    /// the capacity still free in the slot — without booking anything. Used by bulk upload previews.
    /// </summary>
    public async Task<BookingPreview> PreviewAsync(int terminalId, BookingRequest request, CancellationToken ct = default)
    {
        var (p, error) = await PrepareAsync(terminalId, request, ct);
        if (error is not null)
            return new BookingPreview(error, null, new Dictionary<int, int>(), 0, new Dictionary<int, int>(), new Dictionary<int, int>(), new Dictionary<int, int?>());

        var now = clock.GetUtcNow();
        var units = UnitsByCategory(p!.Items);
        var slot = await db.Slots.AsNoTracking().Include(s => s.Categories).SingleAsync(s => s.SlotId == p.Slot.SlotId, ct);
        var (reserved, releaseHours) = await ReservedAndReleaseAsync(slot, p.Company.CompanyId, units.Keys, now, ct);

        var freeByCategory = new Dictionary<int, int>();
        foreach (var categoryId in units.Keys)
        {
            var row = slot.Categories.FirstOrDefault(c => c.CategoryId == categoryId);
            var counters = new SlotCounters(slot.StartAt, slot.EndAt, slot.TotalCapacity, slot.TotalBooked, slot.IsClosed,
                row?.QuotaCapacity ?? 0, row?.Booked ?? 0);
            freeByCategory[categoryId] = AvailabilityCalculator.Available(counters, now, reserved, releaseHours.GetValueOrDefault(categoryId));
        }
        var freeTotal = slot.IsClosed || now >= slot.EndAt ? 0 : Math.Max(0, slot.TotalCapacity - slot.TotalBooked - reserved);

        var cargoTrips = p.Items.Where(i => i.CargoRef is not null)
            .GroupBy(i => p.Cargo[i.CargoRef!].CargoAllocationId)
            .ToDictionary(g => g.Key, g => g.Count());
        var cargoLeft = p.Cargo.Values.ToDictionary(
            c => c.CargoAllocationId,
            c => c.TotalTrips is { } total ? total - c.BookedTrips : (int?)null);

        return new BookingPreview(BookingOutcome.Valid(p.CheckDtos), slot.SlotId, units, freeTotal, freeByCategory, cargoTrips, cargoLeft);
    }

    private async Task<(Prepared? Prepared, BookingOutcome? Error)> PrepareAsync(int terminalId, BookingRequest request, CancellationToken ct)
    {
        var now = clock.GetUtcNow();

        var terminal = await db.Terminals.AsNoTracking().SingleOrDefaultAsync(t => t.TerminalId == terminalId && t.IsActive, ct);
        if (terminal is null)
            return (null, BookingOutcome.NotFound($"Terminal {terminalId} not found."));

        var company = await db.Companies.AsNoTracking().SingleOrDefaultAsync(c => c.CompanyId == request.CompanyId, ct);
        if (company is null)
            return (null, BookingOutcome.NotFound($"Company {request.CompanyId} not found."));
        if (company.Status != CompanyStatus.Active)
            return (null, BookingOutcome.Rejected($"Company {company.Name} is {company.Status.ToString().ToLowerInvariant()} and cannot book."));

        var user = await db.AppUsers.AsNoTracking().SingleOrDefaultAsync(u => u.UserId == request.UserId, ct);
        if (user is null || !CanActFor(user, company.CompanyId, terminalId))
            return (null, BookingOutcome.Invalid("This user cannot book for this company."));

        var slot = await db.Slots.AsNoTracking().SingleOrDefaultAsync(s => s.SlotId == request.SlotId && s.TerminalId == terminalId, ct);
        if (slot is null)
            return (null, BookingOutcome.NotFound($"Slot {request.SlotId} not found at this terminal."));
        if (slot.IsClosed || now >= slot.EndAt)
            return (null, BookingOutcome.NoCapacity("This slot is closed or has already ended."));

        var vehicleError = await CheckTruckAndDriverAsync(company.CompanyId, request.TruckId, request.DriverId, ct);
        if (vehicleError is not null)
            return (null, BookingOutcome.Invalid(vehicleError));

        var categories = await db.TransactionCategories.AsNoTracking()
            .Where(c => c.TerminalId == terminalId && c.IsActive)
            .ToDictionaryAsync(c => c.Code.ToUpperInvariant(), ct);

        var (items, errors) = BookingRequestValidator.Validate(request, categories);
        if (errors.Count > 0)
            return (null, BookingOutcome.Invalid(errors));

        var settings = TerminalSettings.From(
            await db.TerminalSettings.AsNoTracking().Where(s => s.TerminalId == terminalId).ToListAsync(ct));
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(terminal.TimeZone);

        if (request.TruckId is { } truckId && settings.MaxAppointmentsPerTruckPerDay > 0)
        {
            var (dayStart, dayEnd) = LocalDayUtc(slot.StartAt, timeZone);
            var truckCount = await LiveAppointments(terminalId)
                .CountAsync(a => a.TruckId == truckId && a.Slot.StartAt >= dayStart && a.Slot.StartAt < dayEnd, ct);
            if (truckCount >= settings.MaxAppointmentsPerTruckPerDay)
                return (null, BookingOutcome.Conflict($"This truck already has {truckCount} appointment(s) that day (limit {settings.MaxAppointmentsPerTruckPerDay})."));
        }

        var containers = items.Where(i => i.ContainerNo is not null).Select(i => i.ContainerNo!).ToList();
        if (containers.Count > 0)
        {
            var taken = await LiveAppointments(terminalId)
                .SelectMany(a => a.Items)
                .Where(i => i.ContainerNo != null && containers.Contains(i.ContainerNo))
                .Select(i => i.ContainerNo!)
                .ToListAsync(ct);
            if (taken.Count > 0)
                return (null, BookingOutcome.Conflict($"Container {string.Join(", ", taken.Distinct())} already has a live appointment."));
        }

        // Multipurpose cargo references must exist, be open and belong to this company (if assigned).
        var cargoRefs = items.Where(i => i.CargoRef is not null).Select(i => i.CargoRef!).Distinct().ToList();
        var cargo = await db.CargoAllocations.AsNoTracking()
            .Where(c => c.TerminalId == terminalId && cargoRefs.Contains(c.CargoRef))
            .ToDictionaryAsync(c => c.CargoRef, ct);
        foreach (var cargoRef in cargoRefs)
        {
            if (!cargo.TryGetValue(cargoRef, out var allocation))
                return (null, BookingOutcome.Invalid($"Cargo reference {cargoRef} is not known at this terminal."));
            if (allocation.Status != CargoStatus.Open)
                return (null, BookingOutcome.Rejected($"Cargo {cargoRef} is {allocation.Status.ToString().ToLowerInvariant()}."));
            if (allocation.CompanyId is { } owner && owner != company.CompanyId)
                return (null, BookingOutcome.Rejected($"Cargo {cargoRef} is assigned to another company."));
        }

        // Checks: container check digit (internal) + TOS.
        var checks = new List<(int Index, CheckType Type, CheckResult Result, string? Message, string Source, string? TosRef)>();
        foreach (var item in items.Where(i => i.ContainerNo is not null))
        {
            var valid = ContainerNumber.IsValid(item.ContainerNo!);
            checks.Add((item.Index, CheckType.ContainerNumber, valid ? CheckResult.Pass : CheckResult.Fail,
                valid ? null : $"{item.ContainerNo} has a wrong check digit — please re-check the number", InternalSource, null));
        }

        var tosChecks = await tos.ValidateAsync(
            terminalId,
            items.Select(i => new TosItemQuery(i.Index, i.Category.Code, i.Category.Mode, i.ContainerNo, i.BookingRef, i.EdoRef, i.CargoRef, slot.StartAt)).ToList(),
            ct);
        checks.AddRange(tosChecks.Select(c => (c.ItemIndex, c.Type, c.Result, c.Message, tos.Source, c.TosRef)));

        var checkDtos = checks.Select(c => new CheckDto(c.Index + 1, c.Type, c.Result, c.Message, c.Source)).ToList();
        var failures = checks.Where(c => c.Result == CheckResult.Fail).Select(c => $"Item {c.Index + 1}: {c.Message}").ToList();
        if (failures.Count > 0)
            return (null, BookingOutcome.Rejected(failures, checkDtos));

        return (new Prepared(terminal, company, user, slot, items, cargo, checks, checkDtos, settings, timeZone), null);
    }

    private async Task<BookingOutcome> CommitAsync(Prepared p, BookingRequest request, CancellationToken ct)
    {
        var now = clock.GetUtcNow();

        // Consume capacity and save, atomically.
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var unitsByCategory = p.Items.GroupBy(i => i.Category.CategoryId).ToDictionary(g => g.Key, g => g.Count());
        var capacityError = await TryConsumeSlotAsync(p.Slot, p.Company.CompanyId, unitsByCategory, now, ct);
        if (capacityError is not null)
        {
            await tx.RollbackAsync(ct);
            return BookingOutcome.NoCapacity(capacityError);
        }

        foreach (var group in p.Items.Where(i => i.CargoRef is not null).GroupBy(i => i.CargoRef!))
        {
            if (!await counters.TryConsumeCargoTripsAsync(p.Cargo[group.Key].CargoAllocationId, group.Count(), ct))
            {
                await tx.RollbackAsync(ct);
                return BookingOutcome.Rejected($"No trips left to book on cargo {group.Key}.");
            }
        }

        var localSlotDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(p.Slot.StartAt, p.TimeZone).DateTime);
        var appointment = new Appointment
        {
            AppointmentNo = AppointmentNumbers.New(p.Terminal.Code, localSlotDate),
            TerminalId = p.Terminal.TerminalId,
            SlotId = p.Slot.SlotId,
            CompanyId = p.Company.CompanyId,
            TruckId = request.TruckId,
            DriverId = request.DriverId,
            Status = AppointmentStatus.Confirmed,
            IsDual = p.Items.Count > 1,
            QrToken = AppointmentNumbers.NewQrToken(),
            CreatedBy = p.User.UserId,
            CreatedAt = now,
            Items = p.Items.Select(i =>
            {
                var itemChecks = p.Checks.Where(c => c.Index == i.Index).ToList();
                return new AppointmentItem
                {
                    CategoryId = i.Category.CategoryId,
                    ContainerNo = i.ContainerNo,
                    IsoType = i.IsoType,
                    BookingRef = i.BookingRef,
                    EdoRef = i.EdoRef,
                    CargoAllocationId = i.CargoRef is null ? null : p.Cargo[i.CargoRef].CargoAllocationId,
                    Trips = i.CargoRef is null ? null : 1,
                    PlannedTonnage = i.PlannedTonnage,
                    ValidationStatus = TrafficLightCalculator.Summarize(itemChecks.Select(c => c.Result)),
                    TosRef = itemChecks.Select(c => c.TosRef).FirstOrDefault(r => r is not null),
                    ValidationResults = itemChecks.Select(c => new ValidationResult
                    {
                        CheckType = c.Type,
                        Result = c.Result,
                        Message = c.Message,
                        Source = c.Source,
                        CheckedAt = now,
                    }).ToList(),
                };
            }).ToList(),
            History =
            [
                new AppointmentStatusHistory
                {
                    ToStatus = AppointmentStatus.Confirmed, ChangedBy = p.User.UserId, Reason = "Booked", ChangedAt = now,
                },
            ],
        };

        var (light, reason) = TrafficLightCalculator.Calculate(
            appointment.Status,
            appointment.Items.Select((i, n) => new ItemCheckState($"Item {n + 1}", i.ValidationStatus,
                i.ValidationResults.Where(v => v.Result != CheckResult.Pass).Select(v => v.Message).FirstOrDefault())).ToList(),
            p.Slot.StartAt, p.Slot.EndAt, now, p.Settings, p.TimeZone);
        appointment.TrafficLight = light;
        appointment.TrafficReason = Truncate(reason, 200);

        db.Appointments.Add(appointment);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        logger.LogInformation("Booked {AppointmentNo} for company {CompanyId} in slot {SlotId}", appointment.AppointmentNo, p.Company.CompanyId, p.Slot.SlotId);
        return BookingOutcome.Created((await queries.GetAsync(p.Terminal.TerminalId, appointment.AppointmentNo, ct))!);
    }

    // ------------------------------------------------------------------ cancel

    public async Task<BookingOutcome> CancelAsync(int terminalId, string appointmentNo, int userId, string? reason, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var (appointment, user, error) = await LoadForChangeAsync(terminalId, appointmentNo, userId, ct);
        if (error is not null)
            return error;

        var settings = TerminalSettings.From(
            await db.TerminalSettings.AsNoTracking().Where(s => s.TerminalId == terminalId).ToListAsync(ct));
        if (!user!.IsTerminalStaff && now > appointment!.Slot.StartAt.AddMinutes(-settings.CancelCutoffMinutes))
            return BookingOutcome.Conflict(
                $"Cancellation closes {settings.CancelCutoffMinutes} minutes before the slot. You can still move the appointment to another slot.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        if (!await TrySetStatusAsync(appointment!, AppointmentStatus.Cancelled, ct))
        {
            await tx.RollbackAsync(ct);
            return BookingOutcome.Conflict("The appointment changed in the meantime; reload and try again.");
        }

        await ReleaseCapacityAsync(appointment!, ct);
        AddHistory(appointment!, AppointmentStatus.Cancelled, user.UserId, string.IsNullOrWhiteSpace(reason) ? "Cancelled" : reason, now);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return BookingOutcome.Updated((await queries.GetAsync(terminalId, appointment!.AppointmentNo, ct))!);
    }

    // ------------------------------------------------------------------ reschedule

    /// <summary>Moves an appointment to another slot. Allowed until gate-in, even after the original window (late trucks).</summary>
    public async Task<BookingOutcome> RescheduleAsync(int terminalId, string appointmentNo, int userId, long newSlotId, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var (appointment, user, error) = await LoadForChangeAsync(terminalId, appointmentNo, userId, ct);
        if (error is not null)
            return error;

        if (appointment!.SlotId == newSlotId)
            return BookingOutcome.Invalid("The appointment is already in this slot.");

        var newSlot = await db.Slots.AsNoTracking().SingleOrDefaultAsync(s => s.SlotId == newSlotId && s.TerminalId == terminalId, ct);
        if (newSlot is null)
            return BookingOutcome.NotFound($"Slot {newSlotId} not found at this terminal.");
        if (newSlot.IsClosed || now >= newSlot.EndAt)
            return BookingOutcome.NoCapacity("The new slot is closed or has already ended.");

        var units = appointment.UnitsByCategory();
        var oldSlotId = appointment.SlotId;
        var oldStart = appointment.Slot.StartAt;

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var capacityError = await TryConsumeSlotAsync(newSlot, appointment.CompanyId, units, now, ct);
        if (capacityError is not null)
        {
            await tx.RollbackAsync(ct);
            return BookingOutcome.NoCapacity(capacityError);
        }

        var moved = await db.Appointments
            .Where(a => a.AppointmentId == appointment.AppointmentId && a.SlotId == oldSlotId
                        && (a.Status == AppointmentStatus.Confirmed || a.Status == AppointmentStatus.EnRoute || a.Status == AppointmentStatus.Arrived))
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.SlotId, newSlotId), ct);
        if (moved == 0)
        {
            await tx.RollbackAsync(ct);
            return BookingOutcome.Conflict("The appointment changed in the meantime; reload and try again.");
        }

        await counters.ReleaseAsync(oldSlotId, units, ct);

        var terminal = await db.Terminals.AsNoTracking().SingleAsync(t => t.TerminalId == terminalId, ct);
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(terminal.TimeZone);
        db.AppointmentStatusHistory.Add(new AppointmentStatusHistory
        {
            AppointmentId = appointment.AppointmentId,
            FromStatus = appointment.Status,
            ToStatus = appointment.Status,
            ChangedBy = user!.UserId,
            Reason = Truncate($"Moved from {TimeZoneInfo.ConvertTime(oldStart, timeZone):yyyy-MM-dd HH:mm} to {TimeZoneInfo.ConvertTime(newSlot.StartAt, timeZone):yyyy-MM-dd HH:mm}", 300),
            ChangedAt = now,
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return BookingOutcome.Updated((await queries.GetAsync(terminalId, appointment.AppointmentNo, ct))!);
    }

    // ------------------------------------------------------------------ assign truck / driver

    public async Task<BookingOutcome> AssignAsync(
        int terminalId, string appointmentNo, int userId, int? truckId, int? driverId, CancellationToken ct = default)
    {
        var (appointment, _, error) = await LoadForChangeAsync(terminalId, appointmentNo, userId, ct);
        if (error is not null)
            return error;

        var vehicleError = await CheckTruckAndDriverAsync(appointment!.CompanyId, truckId, driverId, ct);
        if (vehicleError is not null)
            return BookingOutcome.Invalid(vehicleError);

        var updated = await db.Appointments
            .Where(a => a.AppointmentId == appointment.AppointmentId
                        && (a.Status == AppointmentStatus.Confirmed || a.Status == AppointmentStatus.EnRoute || a.Status == AppointmentStatus.Arrived))
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.TruckId, truckId)
                .SetProperty(a => a.DriverId, driverId), ct);
        if (updated == 0)
            return BookingOutcome.Conflict("The appointment changed in the meantime; reload and try again.");

        return BookingOutcome.Updated((await queries.GetAsync(terminalId, appointment.AppointmentNo, ct))!);
    }

    // ------------------------------------------------------------------ shared

    internal static bool CanActFor(AppUser user, int companyId, int terminalId) =>
        user.IsActive
        && (user.CompanyId == companyId
            || user.Role == UserRole.SysAdmin
            || (user.IsTerminalStaff && user.TerminalId == terminalId));

    private IQueryable<Appointment> LiveAppointments(int terminalId) =>
        db.Appointments.Where(a => a.TerminalId == terminalId
                                   && (a.Status == AppointmentStatus.Confirmed
                                       || a.Status == AppointmentStatus.EnRoute
                                       || a.Status == AppointmentStatus.Arrived
                                       || a.Status == AppointmentStatus.GateIn));

    private async Task<(Appointment? Appointment, AppUser? User, BookingOutcome? Error)> LoadForChangeAsync(
        int terminalId, string appointmentNo, int userId, CancellationToken ct)
    {
        var number = appointmentNo.Trim().ToUpperInvariant();
        var appointment = await db.Appointments.AsNoTracking()
            .Include(a => a.Slot)
            .Include(a => a.Items)
            .SingleOrDefaultAsync(a => a.TerminalId == terminalId && a.AppointmentNo == number, ct);
        if (appointment is null)
            return (null, null, BookingOutcome.NotFound($"Appointment {number} not found."));

        var user = await db.AppUsers.AsNoTracking().SingleOrDefaultAsync(u => u.UserId == userId, ct);
        if (user is null || !CanActFor(user, appointment.CompanyId, terminalId))
            return (null, null, BookingOutcome.Invalid("This user cannot change this appointment."));

        if (!Appointment.IsBeforeGate(appointment.Status))
            return (null, null, BookingOutcome.Conflict($"An appointment in status {appointment.Status} cannot be changed."));

        return (appointment, user, null);
    }

    private async Task<string?> CheckTruckAndDriverAsync(int companyId, int? truckId, int? driverId, CancellationToken ct)
    {
        if (truckId is not null)
        {
            var truck = await db.Trucks.AsNoTracking().SingleOrDefaultAsync(t => t.TruckId == truckId, ct);
            if (truck is null || truck.CompanyId != companyId)
                return $"Truck {truckId} does not belong to this company.";
            if (truck.IsBlocked)
                return $"Truck {truck.PlateNumber} is blocked at this terminal.";
        }

        if (driverId is not null)
        {
            var driver = await db.Drivers.AsNoTracking().SingleOrDefaultAsync(d => d.DriverId == driverId, ct);
            if (driver is null || driver.CompanyId != companyId)
                return $"Driver {driverId} does not belong to this company.";
            if (driver.IsBlocked)
                return $"Driver {driver.FullName} is blocked at this terminal.";
        }

        return null;
    }

    private static IReadOnlyDictionary<int, int> UnitsByCategory(IEnumerable<ValidatedItem> items) =>
        items.GroupBy(i => i.Category.CategoryId).ToDictionary(g => g.Key, g => g.Count());

    /// <summary>Units held back from this company in the slot, and release hours per category (if a rule exists).</summary>
    private async Task<(int Reserved, IReadOnlyDictionary<int, int?> ReleaseHours)> ReservedAndReleaseAsync(
        Slot slot, int companyId, IEnumerable<int> categoryIds, DateTimeOffset now, CancellationToken ct)
    {
        var reservations = await db.CapacityReservations.AsNoTracking()
            .Where(r => r.TerminalId == slot.TerminalId && r.IsActive && r.StartAt <= slot.StartAt && r.EndAt > slot.StartAt)
            .ToListAsync(ct);
        var reserved = AvailabilityCalculator.ReservedUnits(reservations, slot.StartAt, now, companyId);

        var ids = categoryIds.ToList();
        var rules = await db.QuotaReleaseRules.AsNoTracking()
            .Where(r => r.TerminalId == slot.TerminalId && ids.Contains(r.CategoryId))
            .ToListAsync(ct);
        return (reserved, ids.ToDictionary(id => id, id => rules.FirstOrDefault(r => r.CategoryId == id)?.HoursBefore));
    }

    /// <summary>Consumes slot capacity for a company; returns an error message when there is no room.</summary>
    private async Task<string?> TryConsumeSlotAsync(
        Slot slot, int companyId, IReadOnlyDictionary<int, int> unitsByCategory, DateTimeOffset now, CancellationToken ct)
    {
        var (reserved, releaseHours) = await ReservedAndReleaseAsync(slot, companyId, unitsByCategory.Keys, now, ct);
        var released = releaseHours
            .Where(r => AvailabilityCalculator.IsQuotaReleased(slot.StartAt, now, r.Value))
            .Select(r => r.Key)
            .ToHashSet();

        return await counters.TryConsumeAsync(slot.SlotId, unitsByCategory, reserved, released, ct)
            ? null
            : "No capacity left in this slot for this booking. Please choose another slot.";
    }

    /// <summary>Gives back slot and cargo capacity. Must run inside the caller's transaction.</summary>
    internal async Task ReleaseCapacityAsync(Appointment appointment, CancellationToken ct)
    {
        await counters.ReleaseAsync(appointment.SlotId, appointment.UnitsByCategory(), ct);
        foreach (var group in appointment.Items.Where(i => i.CargoAllocationId is not null).GroupBy(i => i.CargoAllocationId!.Value))
            await counters.ReleaseCargoTripsAsync(group.Key, group.Sum(i => i.Trips ?? 1), ct);
    }

    /// <summary>Moves a pre-gate appointment to a final status only if nobody changed it meanwhile.</summary>
    internal async Task<bool> TrySetStatusAsync(Appointment appointment, AppointmentStatus status, CancellationToken ct)
    {
        var reason = status == AppointmentStatus.Cancelled ? "Cancelled" : "Missed — the truck did not arrive in its window";
        var rows = await db.Appointments
            .Where(a => a.AppointmentId == appointment.AppointmentId
                        && (a.Status == AppointmentStatus.Confirmed || a.Status == AppointmentStatus.EnRoute || a.Status == AppointmentStatus.Arrived))
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.Status, status)
                .SetProperty(a => a.TrafficLight, TrafficLight.Grey)
                .SetProperty(a => a.TrafficReason, reason), ct);
        return rows > 0;
    }

    internal void AddHistory(Appointment appointment, AppointmentStatus to, int? userId, string reason, DateTimeOffset now) =>
        db.AppointmentStatusHistory.Add(new AppointmentStatusHistory
        {
            AppointmentId = appointment.AppointmentId,
            FromStatus = appointment.Status,
            ToStatus = to,
            ChangedBy = userId,
            Reason = Truncate(reason, 300),
            ChangedAt = now,
        });

    private static (DateTimeOffset Start, DateTimeOffset End) LocalDayUtc(DateTimeOffset instant, TimeZoneInfo timeZone)
    {
        var localDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, timeZone).DateTime);
        var start = new DateTimeOffset(
            TimeZoneInfo.ConvertTimeToUtc(localDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), timeZone), TimeSpan.Zero);
        return (start, start.AddDays(1));
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
