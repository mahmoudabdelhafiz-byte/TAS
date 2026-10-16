using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Tas.Application.Auth;
using Tas.Application.Booking;
using Tas.Application.Tos;
using Tas.Domain;
using Tas.Domain.Booking;
using Tas.Application.Notifications;
using Tas.Infrastructure.Notifications;
using Tas.Infrastructure.Persistence;

namespace Tas.Infrastructure.Booking;

/// <param name="Changes">What changed since the last check, e.g. "MSCU1234566: Pass → Fail (Customs hold on MSCU1234566)".</param>
public sealed record RevalidationResult(string AppointmentNo, TrafficLight Light, string Reason, IReadOnlyList<string> Changes);

/// <summary>
/// Re-checks booked appointments against the TOS before the truck arrives: a container that was clear at
/// booking may have a customs hold, a withdrawn release or unpaid charges by the time the truck leaves.
///
/// - Scheduled: appointments starting within Revalidation:LookAheadHours (24) are re-checked when their last
///   check is older than Revalidation:MinAgeMinutes (60).
/// - On demand: a dispatcher or terminal staff can re-check one appointment.
/// - Start trip: re-checks right before the truck sets off and refuses to start while anything is red.
///
/// When an item turns red (or clears), the change goes into the appointment history and a notification is
/// queued for the driver and the person who booked.
/// </summary>
public sealed class RevalidationService(
    TasDbContext db,
    ITosAdapter tos,
    AppointmentQueryService queries,
    TimeProvider clock,
    IConfiguration config,
    ILogger<RevalidationService> logger)
{
    /// <summary>A trip can be started at most this many hours before the slot.</summary>
    public const int StartTripEarliestHours = 12;

    public async Task<RevalidationResult?> RevalidateAsync(long appointmentId, string trigger, CancellationToken ct = default)
    {
        db.ChangeTracker.Clear();
        var a = await db.Appointments
            .Include(x => x.Slot)
            .Include(x => x.Driver)
            .Include(x => x.Items).ThenInclude(i => i.Category)
            .Include(x => x.Items).ThenInclude(i => i.CargoAllocation)
            .Include(x => x.Items).ThenInclude(i => i.ValidationResults)
            .AsSplitQuery()
            .SingleOrDefaultAsync(x => x.AppointmentId == appointmentId, ct);
        if (a is null || !Appointment.IsBeforeGate(a.Status))
            return null;

        var terminal = await db.Terminals.AsNoTracking().SingleAsync(t => t.TerminalId == a.TerminalId, ct);
        var settings = TerminalSettings.From(
            await db.TerminalSettings.AsNoTracking().Where(s => s.TerminalId == a.TerminalId).ToListAsync(ct));
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(terminal.TimeZone);
        var now = clock.GetUtcNow();

        var items = a.Items.OrderBy(i => i.ItemId).ToList();
        var checks = await ItemChecks.RunAsync(
            tos,
            a.TerminalId,
            items.Select((i, n) => new ItemToCheck(n, i.Category.Code, i.Category.Mode, i.ContainerNo, i.BookingRef, i.EdoRef, i.CargoAllocation?.CargoRef)).ToList(),
            a.Slot.StartAt,
            ct);

        var changes = new List<string>();
        var turnedRed = false;
        var cleared = false;

        for (var n = 0; n < items.Count; n++)
        {
            var item = items[n];
            var itemChecks = checks.Where(c => c.Index == n).ToList();
            var status = TrafficLightCalculator.Summarize(itemChecks.Select(c => c.Result));

            if (status != item.ValidationStatus)
            {
                var what = item.ContainerNo ?? item.CargoAllocation?.CargoRef ?? item.BookingRef ?? $"Item {n + 1}";
                var problem = itemChecks.Where(c => c.Result != CheckResult.Pass).OrderByDescending(c => c.Result).Select(c => c.Message).FirstOrDefault();
                changes.Add(problem is null ? $"{what}: {item.ValidationStatus} → {status}" : $"{what}: {item.ValidationStatus} → {status} ({problem})");
                turnedRed |= status == ValidationStatus.Fail;
                cleared |= item.ValidationStatus == ValidationStatus.Fail && status != ValidationStatus.Fail;
            }

            db.ValidationResults.RemoveRange(item.ValidationResults);
            item.ValidationResults = itemChecks.Select(c => new ValidationResult
            {
                ItemId = item.ItemId,
                CheckType = c.Type,
                Result = c.Result,
                Message = c.Message,
                Source = c.Source,
                CheckedAt = now,
            }).ToList();
            item.ValidationStatus = status;
            item.TosRef = itemChecks.Select(c => c.TosRef).FirstOrDefault(r => r is not null) ?? item.TosRef;
        }

        var (light, reason) = AppointmentQueryService.Traffic(a, settings, timeZone, now);
        a.TrafficLight = light;
        a.TrafficReason = Truncate(reason, 200);
        a.LastValidatedAt = now;

        if (changes.Count > 0)
        {
            db.AppointmentStatusHistory.Add(new AppointmentStatusHistory
            {
                AppointmentId = a.AppointmentId,
                FromStatus = a.Status,
                ToStatus = a.Status,
                Reason = Truncate($"Re-check ({trigger}): {string.Join("; ", changes)}", 300),
                ChangedAt = now,
            });

            var anyFailing = items.Any(i => i.ValidationStatus == ValidationStatus.Fail);
            var template = turnedRed ? NotificationTemplates.StatusRed
                : cleared && !anyFailing ? NotificationTemplates.StatusCleared
                : null;
            if (template is not null)
            {
                var values = new Dictionary<string, string>
                {
                    [NotificationText.Keys.AppointmentNo] = a.AppointmentNo,
                    [NotificationText.Keys.Slot] = NotificationText.SlotLabel(a.Slot.StartAt, a.Slot.EndAt, timeZone),
                    [NotificationText.Keys.Reason] = reason,
                };
                await NotificationQueue.ForAppointmentAsync(db, a.AppointmentId, template, values, await NotificationQueue.DriverChannelAsync(db, config, a.TerminalId, ct), now, ct,
                    pushFirst: NotificationQueue.PushFirst(config));
            }

            logger.LogInformation("Re-check of {AppointmentNo} ({Trigger}): {Changes}", a.AppointmentNo, trigger, string.Join("; ", changes));
        }

        await db.SaveChangesAsync(ct);
        return new RevalidationResult(a.AppointmentNo, light, reason, changes);
    }

    /// <summary>Re-checks the appointments that are due. Returns how many were checked.</summary>
    public async Task<int> RunDueAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var horizon = now.AddHours(config.GetValue("Revalidation:LookAheadHours", 24.0));
        var staleBefore = now.AddMinutes(-config.GetValue("Revalidation:MinAgeMinutes", 60.0));
        var batch = config.GetValue("Revalidation:BatchSize", 200);

        var due = await db.Appointments.AsNoTracking()
            .Where(a => (a.Status == AppointmentStatus.Confirmed || a.Status == AppointmentStatus.EnRoute || a.Status == AppointmentStatus.Arrived)
                        && a.Slot.StartAt <= horizon
                        && a.Slot.EndAt > now
                        && (a.LastValidatedAt == null || a.LastValidatedAt < staleBefore))
            .OrderBy(a => a.Slot.StartAt)
            .Take(batch)
            .Select(a => a.AppointmentId)
            .ToListAsync(ct);

        var checkedCount = 0;
        foreach (var id in due)
        {
            try
            {
                if (await RevalidateAsync(id, "scheduled", ct) is not null)
                    checkedCount++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // e.g. the TOS is unreachable: leave it due and try again next run.
                logger.LogError(ex, "Re-check of appointment {AppointmentId} failed", id);
            }
        }
        return checkedCount;
    }

    /// <summary>Re-checks one appointment now, on request of a dispatcher or terminal staff.</summary>
    public async Task<BookingOutcome> RevalidateByNumberAsync(int terminalId, string appointmentNo, CurrentUser user, CancellationToken ct = default)
    {
        var number = appointmentNo.Trim().ToUpperInvariant();
        var query = db.Appointments.AsNoTracking().Where(a => a.TerminalId == terminalId && a.AppointmentNo == number);
        if (user.IsCompanyUser)
            query = query.Where(a => a.CompanyId == user.CompanyId);
        var id = await query.Select(a => (long?)a.AppointmentId).SingleOrDefaultAsync(ct);
        if (id is null)
            return BookingOutcome.NotFound($"Appointment {number} not found.");

        try
        {
            if (await RevalidateAsync(id.Value, "on request", ct) is null)
                return BookingOutcome.Conflict("Only appointments that have not passed the gate can be re-checked.");
        }
        catch (TosUnavailableException)
        {
            return BookingOutcome.Conflict("The terminal system can't be reached right now; please try again in a few minutes.");
        }

        return BookingOutcome.Updated((await queries.GetAsync(terminalId, number, ct))!);
    }

    /// <summary>
    /// The truck is about to leave: re-check now and mark the appointment EN_ROUTE — unless anything is red
    /// (hold, not available, window passed), in which case the truck should not set off.
    /// </summary>
    public async Task<BookingOutcome> StartTripAsync(int terminalId, string appointmentNo, CurrentUser user, CancellationToken ct = default)
    {
        var number = appointmentNo.Trim().ToUpperInvariant();
        var query = db.Appointments.AsNoTracking().Include(a => a.Slot)
            .Where(a => a.TerminalId == terminalId && a.AppointmentNo == number);
        if (user.IsCompanyUser)
            query = query.Where(a => a.CompanyId == user.CompanyId);
        var appointment = await query.SingleOrDefaultAsync(ct);
        if (appointment is null)
            return BookingOutcome.NotFound($"Appointment {number} not found.");

        return await StartTripCoreAsync(appointment, user.UserId, "Trip started", ct);
    }

    /// <summary>Start trip from the driver app (the caller has checked the appointment is this driver's).</summary>
    public async Task<BookingOutcome> StartTripForDriverAsync(long appointmentId, CancellationToken ct = default)
    {
        var appointment = await db.Appointments.AsNoTracking().Include(a => a.Slot)
            .SingleOrDefaultAsync(a => a.AppointmentId == appointmentId, ct);
        return appointment is null
            ? BookingOutcome.NotFound("Appointment not found.")
            : await StartTripCoreAsync(appointment, null, "Trip started (driver app)", ct);
    }

    private async Task<BookingOutcome> StartTripCoreAsync(Appointment appointment, int? actorUserId, string reason, CancellationToken ct)
    {
        var terminalId = appointment.TerminalId;
        var number = appointment.AppointmentNo;
        if (appointment.Status is not (AppointmentStatus.Confirmed or AppointmentStatus.EnRoute))
            return BookingOutcome.Conflict($"A trip can't be started for an appointment in status {appointment.Status}.");

        var now = clock.GetUtcNow();
        if (now < appointment.Slot.StartAt.AddHours(-StartTripEarliestHours))
            return BookingOutcome.Conflict($"Too early: a trip can be started from {StartTripEarliestHours} hours before the slot.");

        RevalidationResult? check;
        try
        {
            check = await RevalidateAsync(appointment.AppointmentId, "trip start", ct);
        }
        catch (TosUnavailableException)
        {
            return BookingOutcome.Conflict("The terminal system can't be reached to confirm the containers are clear; please try again in a few minutes before setting off.");
        }
        if (check is null)
            return BookingOutcome.Conflict("The appointment changed in the meantime; reload and try again.");
        if (check.Light == TrafficLight.Red)
            return BookingOutcome.Rejected($"Don't set off: {check.Reason}");

        if (appointment.Status == AppointmentStatus.Confirmed)
        {
            var started = await db.Appointments
                .Where(a => a.AppointmentId == appointment.AppointmentId && a.Status == AppointmentStatus.Confirmed)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, AppointmentStatus.EnRoute), ct);
            if (started > 0)
            {
                db.AppointmentStatusHistory.Add(new AppointmentStatusHistory
                {
                    AppointmentId = appointment.AppointmentId,
                    FromStatus = AppointmentStatus.Confirmed,
                    ToStatus = AppointmentStatus.EnRoute,
                    ChangedBy = actorUserId,
                    Reason = reason,
                    ChangedAt = now,
                });
                await db.SaveChangesAsync(ct);
            }
        }

        return BookingOutcome.Updated((await queries.GetAsync(terminalId, number, ct))!);
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}

/// <summary>Runs the scheduled re-checks. Config: Revalidation:Enabled (default true), Revalidation:IntervalMinutes (default 15).</summary>
public sealed class RevalidationWorker(IServiceScopeFactory scopes, IConfiguration config, ILogger<RevalidationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.GetValue("Revalidation:Enabled", true))
            return;

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(config.GetValue("Revalidation:IntervalMinutes", 15.0)));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                var count = await scope.ServiceProvider.GetRequiredService<RevalidationService>().RunDueAsync(stoppingToken);
                if (count > 0)
                    logger.LogInformation("Re-checked {Count} upcoming appointment(s)", count);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Re-validation run failed");
            }
        }
    }
}
