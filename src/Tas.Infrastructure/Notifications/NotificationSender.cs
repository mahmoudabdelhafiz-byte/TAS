using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Tas.Application.Notifications;
using Tas.Domain;
using Tas.Domain.Booking;
using Tas.Infrastructure.Persistence;

namespace Tas.Infrastructure.Notifications;

public sealed record NotificationRunResult(int Claimed, int Sent, int Retrying, int Failed, int Skipped);

/// <summary>
/// Delivers the notification outbox:
/// 1. Claims a batch of due QUEUED rows (FOR UPDATE SKIP LOCKED, so several API instances never send the same
///    message twice) and leases them for Notifications:LeaseMinutes.
/// 2. Skips messages that no longer apply (appointment cancelled / done, slot passed, superseded, too old).
/// 3. Renders the template in the recipient's language and sends it through the channel's provider.
/// 4. Marks SENT; or keeps QUEUED with a back-off for transient errors; FAILED for permanent errors or after
///    Notifications:MaxAttempts.
/// </summary>
public sealed class NotificationSender(
    TasDbContext db,
    IEnumerable<INotificationChannel> channels,
    IOptions<NotificationOptions> options,
    TimeProvider clock,
    ILogger<NotificationSender> logger)
{
    private enum Outcome { Sent, Retrying, Failed, Skipped, Gone }

    public async Task<NotificationRunResult> RunAsync(CancellationToken ct = default)
    {
        var ids = await ClaimAsync(ct);
        int sent = 0, retrying = 0, failed = 0, skipped = 0;

        foreach (var id in ids)
        {
            Outcome outcome;
            try
            {
                outcome = await ProcessAsync(id, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The lease expires and the message is picked up again; attempts keep counting.
                logger.LogError(ex, "Notification {NotificationId} could not be processed", id);
                continue;
            }

            switch (outcome)
            {
                case Outcome.Sent: sent++; break;
                case Outcome.Retrying: retrying++; break;
                case Outcome.Failed: failed++; break;
                case Outcome.Skipped: skipped++; break;
            }
        }

        if (ids.Count > 0)
            logger.LogInformation("Notifications: {Sent} sent, {Retrying} to retry, {Failed} failed, {Skipped} skipped", sent, retrying, failed, skipped);
        return new NotificationRunResult(ids.Count, sent, retrying, failed, skipped);
    }

    /// <summary>Atomically takes due messages for this sender and counts the attempt.</summary>
    private async Task<IReadOnlyList<long>> ClaimAsync(CancellationToken ct)
    {
        var o = options.Value;
        var now = clock.GetUtcNow();
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var opened = connection.State != ConnectionState.Open;
        if (opened)
            await connection.OpenAsync(ct);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE notification_log
                   SET attempts = attempts + 1, next_attempt_at = @lease
                 WHERE notification_id IN (
                       SELECT notification_id FROM notification_log
                        WHERE status = 'QUEUED' AND (next_attempt_at IS NULL OR next_attempt_at <= @now)
                        ORDER BY created_at, notification_id
                        LIMIT @batch
                          FOR UPDATE SKIP LOCKED)
                RETURNING notification_id
                """;
            command.Parameters.AddWithValue("now", now.ToUniversalTime());
            command.Parameters.AddWithValue("lease", now.AddMinutes(o.LeaseMinutes).ToUniversalTime());
            command.Parameters.AddWithValue("batch", Math.Max(1, o.BatchSize));

            var ids = new List<long>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                ids.Add(reader.GetInt64(0));
            ids.Sort();
            return ids;
        }
        finally
        {
            if (opened)
                await connection.CloseAsync();
        }
    }

    private async Task<Outcome> ProcessAsync(long id, CancellationToken ct)
    {
        var o = options.Value;
        var now = clock.GetUtcNow();
        db.ChangeTracker.Clear();

        var n = await db.NotificationLogs.SingleOrDefaultAsync(x => x.NotificationId == id, ct);
        if (n is null || n.Status != NotificationStatus.Queued)
            return Outcome.Gone;

        if (n.AppointmentId is not { } appointmentId)
            return await FinishAsync(n, Outcome.Failed, "Not linked to an appointment.", ct);

        var a = await db.Appointments.AsNoTracking()
            .Include(x => x.Slot)
            .Include(x => x.Truck)
            .Include(x => x.Items).ThenInclude(i => i.CargoAllocation)
            .Include(x => x.Items).ThenInclude(i => i.ValidationResults)
            .AsSplitQuery()
            .SingleOrDefaultAsync(x => x.AppointmentId == appointmentId, ct);
        if (a is null)
            return await FinishAsync(n, Outcome.Failed, "Appointment not found.", ct);

        var superseded = n.TemplateCode is NotificationTemplates.StatusRed or NotificationTemplates.StatusCleared
                         && await db.NotificationLogs.AsNoTracking().AnyAsync(x =>
                             x.AppointmentId == n.AppointmentId && x.Channel == n.Channel && x.DriverId == n.DriverId && x.UserId == n.UserId
                             && (x.TemplateCode == NotificationTemplates.StatusRed || x.TemplateCode == NotificationTemplates.StatusCleared)
                             && x.NotificationId > n.NotificationId, ct);
        var anyFailing = a.Items.Any(i => i.ValidationStatus == ValidationStatus.Fail);

        var skip = NotificationRelevance.SkipReason(
            n.TemplateCode, a.Status, a.Slot.EndAt, anyFailing, superseded, n.CreatedAt, now, TimeSpan.FromHours(o.MaxAgeHours));
        if (skip is not null)
            return await FinishAsync(n, Outcome.Skipped, skip, ct);

        // Recipient
        string? to;
        if (n.DriverId is { } driverId)
        {
            var mobile = await db.Drivers.AsNoTracking().Where(d => d.DriverId == driverId).Select(d => d.Mobile).SingleOrDefaultAsync(ct);
            to = n.Channel == NotificationChannel.Email ? null : mobile;
        }
        else if (n.UserId is { } userId)
        {
            var user = await db.AppUsers.AsNoTracking().Where(u => u.UserId == userId).Select(u => new { u.Email, u.Mobile, u.IsActive }).SingleOrDefaultAsync(ct);
            if (user is { IsActive: false })
                return await FinishAsync(n, Outcome.Skipped, "The user has been deactivated.", ct);
            to = n.Channel == NotificationChannel.Email ? user?.Email : user?.Mobile;
        }
        else
        {
            to = null;
        }

        if (string.IsNullOrWhiteSpace(to))
            return await FinishAsync(n, Outcome.Failed, n.Channel == NotificationChannel.Email ? "No email address for the recipient." : "No mobile number for the recipient.", ct);
        n.Recipient = Truncate(to.Trim(), 200);

        // Render
        var terminal = await db.Terminals.AsNoTracking().Where(t => t.TerminalId == a.TerminalId).Select(t => new { t.Name, t.TimeZone }).SingleAsync(ct);
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(terminal.TimeZone);
        var problems = a.Items
            .Where(i => i.ValidationStatus == ValidationStatus.Fail)
            .OrderBy(i => i.ItemId)
            .SelectMany(i => i.ValidationResults
                .Where(r => r.Result == CheckResult.Fail)
                .Select(r => new NotificationProblem(i.ContainerNo ?? i.CargoAllocation?.CargoRef ?? i.BookingRef ?? $"#{i.ItemId}", r.CheckType)))
            .ToList();
        var data = new NotificationData(
            a.AppointmentNo,
            terminal.Name,
            TimeZoneInfo.ConvertTime(a.Slot.StartAt, timeZone).DateTime,
            TimeZoneInfo.ConvertTime(a.Slot.EndAt, timeZone).DateTime,
            a.Truck?.PlateNumber,
            problems);

        var message = NotificationRenderer.Render(n.TemplateCode, n.Language, n.Channel, data);
        if (message is null)
            return await FinishAsync(n, Outcome.Failed, $"Unknown template '{n.TemplateCode}'.", ct);

        var channel = channels.FirstOrDefault(c => c.Channel == n.Channel);
        if (channel is null)
            return await FinishAsync(n, Outcome.Failed, $"No provider is configured for {n.Channel.ToString().ToUpperInvariant()} (Notifications settings).", ct);

        DeliveryResult result;
        try
        {
            result = await channel.SendAsync(new OutgoingMessage(n.NotificationId, n.Channel, to.Trim(), message), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            result = DeliveryResult.Retry($"{ex.GetType().Name}: {ex.Message}");
        }

        if (result.Ok)
        {
            n.Status = NotificationStatus.Sent;
            n.SentAt = clock.GetUtcNow();
            n.ProviderRef = result.ProviderRef is null ? null : Truncate(result.ProviderRef, 100);
            n.LastError = null;
            n.NextAttemptAt = null;
            await db.SaveChangesAsync(ct);
            return Outcome.Sent;
        }

        if (result.Permanent || NotificationRetry.Exhausted(n.Attempts, o.MaxAttempts))
        {
            logger.LogWarning("Notification {NotificationId} failed after {Attempts} attempt(s): {Error}", n.NotificationId, n.Attempts, result.Error);
            return await FinishAsync(n, Outcome.Failed, result.Error ?? "Delivery failed.", ct);
        }

        n.LastError = Truncate(result.Error ?? "Delivery failed.", 300);
        n.NextAttemptAt = now + NotificationRetry.DelayAfter(n.Attempts);
        await db.SaveChangesAsync(ct);
        return Outcome.Retrying;
    }

    private async Task<Outcome> FinishAsync(NotificationLog n, Outcome outcome, string reason, CancellationToken ct)
    {
        n.Status = outcome == Outcome.Skipped ? NotificationStatus.Skipped : NotificationStatus.Failed;
        n.LastError = Truncate(reason, 300);
        n.NextAttemptAt = null;
        await db.SaveChangesAsync(ct);
        return outcome;
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}

/// <summary>
/// Queues SLOT_REMINDER messages Notifications:ReminderHoursBefore (2) hours before the slot: SMS to the driver,
/// or — if no driver is assigned yet — email to the person who booked. Appointments booked inside that window
/// get no reminder.
/// </summary>
public sealed class SlotReminderService(TasDbContext db, IOptions<NotificationOptions> options, TimeProvider clock, ILogger<SlotReminderService> logger)
{
    public async Task<int> QueueDueAsync(CancellationToken ct = default)
    {
        var hours = options.Value.ReminderHoursBefore;
        if (hours <= 0)
            return 0;

        var now = clock.GetUtcNow();
        var window = TimeSpan.FromHours(hours);
        var horizon = now + window;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Only one instance queues reminders at a time (transaction-scoped advisory lock).
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7340033)", ct);

        var candidates = await db.Appointments.AsNoTracking()
            .Where(a => (a.Status == AppointmentStatus.Confirmed || a.Status == AppointmentStatus.EnRoute)
                        && a.Slot.StartAt > now && a.Slot.StartAt <= horizon
                        && !db.NotificationLogs.Any(n => n.AppointmentId == a.AppointmentId && n.TemplateCode == NotificationTemplates.SlotReminder))
            .Select(a => new { a.AppointmentId, a.DriverId, a.CreatedBy, a.CreatedAt, a.Slot.StartAt })
            .ToListAsync(ct);

        var queued = 0;
        foreach (var a in candidates.Where(a => a.CreatedAt < a.StartAt - window))
        {
            queued += await NotificationOutbox.QueueAsync(
                db, a.AppointmentId, a.DriverId, a.CreatedBy, NotificationTemplates.SlotReminder, now, ct,
                toDriver: true, toBooker: a.DriverId is null);
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        if (queued > 0)
            logger.LogInformation("Queued {Count} slot reminder(s)", queued);
        return queued;
    }
}

/// <summary>
/// Queues reminders and delivers the outbox. Config: Notifications:Enabled (true), Notifications:IntervalSeconds (30).
/// </summary>
public sealed class NotificationWorker(IServiceScopeFactory scopes, IOptions<NotificationOptions> options, ILogger<NotificationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        if (!o.Enabled)
            return;

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(5, o.IntervalSeconds)));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<SlotReminderService>().QueueDueAsync(stoppingToken);

                // Keep going while full batches come back, up to 10 batches per tick.
                var sender = scope.ServiceProvider.GetRequiredService<NotificationSender>();
                for (var i = 0; i < 10; i++)
                {
                    var run = await sender.RunAsync(stoppingToken);
                    if (run.Claimed < o.BatchSize)
                        break;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Notification run failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
