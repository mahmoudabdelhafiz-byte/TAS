using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tas.Application.Notifications;
using Tas.Domain;
using Tas.Domain.Booking;
using Tas.Infrastructure.Persistence;

namespace Tas.Infrastructure.Notifications;

public sealed record DispatchResult(int Sent, int Retrying, int Failed);

/// <summary>
/// Delivers the notification outbox. Each due message is claimed (so two API instances never send it twice),
/// rendered in the recipient's language, sent through the channel's provider and marked SENT; temporary failures
/// are retried after 1, 5, 15 and 60 minutes, permanent ones (bad number, unconfigured provider) are marked FAILED.
/// Also queues driver reminders shortly before their slot.
/// </summary>
public sealed class NotificationDispatcher(
    TasDbContext db,
    NotificationSenders senders,
    IOptionsMonitor<NotificationOptions> options,
    TimeProvider clock,
    ILogger<NotificationDispatcher> logger,
    TerminalProfiles? profiles = null,
    Brands.BrandingService? branding = null)
{
    private static readonly TimeSpan[] Backoff =
        [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(60)];

    /// <summary>How long a claimed message is hidden from other dispatchers while being sent.</summary>
    private static readonly TimeSpan ClaimFor = TimeSpan.FromMinutes(5);

    public async Task<DispatchResult> DispatchDueAsync(CancellationToken ct = default)
    {
        var o = options.CurrentValue;
        var now = clock.GetUtcNow();

        var due = await db.NotificationLogs.AsNoTracking()
            .Where(n => n.Status == NotificationStatus.Queued && (n.NextAttemptAt == null || n.NextAttemptAt <= now))
            .OrderBy(n => n.NotificationId)
            .Select(n => n.NotificationId)
            .Take(Math.Max(1, o.BatchSize))
            .ToListAsync(ct);

        int sent = 0, retrying = 0, failed = 0;
        foreach (var id in due)
        {
            // Claim: only the dispatcher that moves next_attempt_at forward sends it.
            var claimed = await db.NotificationLogs
                .Where(n => n.NotificationId == id && n.Status == NotificationStatus.Queued && (n.NextAttemptAt == null || n.NextAttemptAt <= now))
                .ExecuteUpdateAsync(s => s.SetProperty(n => n.NextAttemptAt, now + ClaimFor), ct);
            if (claimed == 0)
                continue;

            switch (await SendOneAsync(id, o, now, ct))
            {
                case NotificationStatus.Sent:
                    sent++;
                    break;
                case NotificationStatus.Failed:
                    failed++;
                    break;
                default:
                    retrying++;
                    break;
            }
        }

        if (sent + retrying + failed > 0)
            logger.LogInformation("Notifications: {Sent} sent, {Retrying} to retry, {Failed} failed", sent, retrying, failed);
        return new DispatchResult(sent, retrying, failed);
    }

    private async Task<NotificationStatus> SendOneAsync(long id, NotificationOptions o, DateTimeOffset now, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        var n = await db.NotificationLogs.SingleAsync(x => x.NotificationId == id, ct);

        NotificationStatus Fail(string error)
        {
            n.Status = NotificationStatus.Failed;
            n.LastError = Truncate(error, 400);
            n.NextAttemptAt = null;
            return NotificationStatus.Failed;
        }

        NotificationStatus result;
        if (n.CreatedAt < now.AddHours(-Math.Max(1, o.ExpireHours)))
        {
            result = Fail($"Expired: not sent within {o.ExpireHours} h.");
        }
        else if (!NotificationText.IsKnown(n.TemplateCode))
        {
            result = Fail($"Unknown template {n.TemplateCode}.");
        }
        else if (n.TemplateCode == NotificationTemplates.SlotReminder && !await AppointmentStillUpcomingAsync(n.AppointmentId, ct))
        {
            result = Fail("Appointment no longer upcoming; reminder dropped.");
        }
        else if (senders.For(n.Channel, await TerminalCodeAsync(n.TerminalId, ct)) is not { } sender)
        {
            result = Fail($"No provider configured for {n.Channel}.");
        }
        else if (await RecipientAsync(n, ct) is not { } recipient)
        {
            result = n.Channel == NotificationChannel.Push
                ? PushFallback(n, o, now, "The driver is no longer signed in to the app.", Fail)
                : Fail($"No {(n.Channel == NotificationChannel.Email ? "email address" : "mobile number")} for the recipient.");
        }
        else
        {
            n.Recipient = Truncate(recipient, 200);
            n.Attempts++;
            var values = Values(n.Params);
            try
            {
                var brand = branding is not null
                    ? await branding.MessageNameAsync(n.TerminalId, ct)
                    : profiles?.BrandFor(await TerminalCodeAsync(n.TerminalId, ct));
                var message = NotificationText.Render(n.TemplateCode, n.Language, values, brand) with
                {
                    Data = new Dictionary<string, string>
                    {
                        ["template"] = n.TemplateCode,
                        ["appointmentNo"] = values.GetValueOrDefault(NotificationText.Keys.AppointmentNo) ?? "",
                    },
                };
                n.ProviderRef = Truncate(await sender.SendAsync(recipient, message, ct), 100);
                n.Status = NotificationStatus.Sent;
                n.SentAt = clock.GetUtcNow();
                n.LastError = null;
                n.NextAttemptAt = null;
                result = NotificationStatus.Sent;
            }
            catch (NotificationSendException ex) when (ex.Permanent && n.Channel == NotificationChannel.Push)
            {
                if (ex.InvalidRecipient)
                {
                    // The app was removed or reinstalled: stop pushing to that token.
                    await db.DriverDevices.Where(d => d.PushToken == recipient).ExecuteUpdateAsync(s => s.SetProperty(d => d.PushToken, (string?)null), ct);
                }
                result = PushFallback(n, o, now, $"{sender.Provider}: {ex.Message}", Fail);
            }
            catch (NotificationSendException ex) when (ex.Permanent || n.Attempts >= o.MaxAttempts)
            {
                result = Fail($"{sender.Provider}: {ex.Message}");
            }
            catch (Exception ex) when (ex is NotificationSendException or HttpRequestException or TimeoutException)
            {
                n.LastError = Truncate($"{sender.Provider}: {ex.Message}", 400);
                n.NextAttemptAt = now + Backoff[Math.Min(n.Attempts - 1, Backoff.Length - 1)];
                result = n.Attempts >= o.MaxAttempts ? Fail(n.LastError!) : NotificationStatus.Queued;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // e.g. an invalid email address: retrying won't help, and the message must not block the queue.
                logger.LogWarning(ex, "Notification {Id} could not be sent", n.NotificationId);
                result = Fail($"{sender.Provider}: {ex.Message}");
            }
        }

        await db.SaveChangesAsync(ct);
        return result;
    }

    /// <summary>A push that can't be delivered: marked FAILED and the same message queued by SMS / WhatsApp instead.</summary>
    private NotificationStatus PushFallback(
        NotificationLog n, NotificationOptions o, DateTimeOffset now, string error, Func<string, NotificationStatus> fail)
    {
        var result = fail(error);
        if (n.DriverId is not null)
        {
            db.NotificationLogs.Add(new NotificationLog
            {
                AppointmentId = n.AppointmentId,
                TerminalId = n.TerminalId,
                DriverId = n.DriverId,
                Channel = DriverChannel(o, n.TerminalId),
                TemplateCode = n.TemplateCode,
                Language = n.Language,
                Status = NotificationStatus.Queued,
                Params = n.Params,
                CreatedAt = now,
            });
            n.LastError = Truncate($"{error} Sent by {DriverChannel(o, n.TerminalId)} instead.", 400);
        }
        return result;
    }

    /// <summary>Queues a reminder for each upcoming appointment with a driver that hasn't had one.</summary>
    public async Task<int> QueueRemindersAsync(CancellationToken ct = default)
    {
        var o = options.CurrentValue;
        if (o.ReminderHoursBefore <= 0)
            return 0;

        var now = clock.GetUtcNow();
        var until = now.AddHours(o.ReminderHoursBefore);

        var upcoming = await db.Appointments.AsNoTracking()
            .Where(a => (a.Status == AppointmentStatus.Confirmed || a.Status == AppointmentStatus.EnRoute)
                        && a.DriverId != null
                        && a.Slot.StartAt > now && a.Slot.StartAt <= until
                        && !db.NotificationLogs.Any(n => n.AppointmentId == a.AppointmentId && n.TemplateCode == NotificationTemplates.SlotReminder))
            .Select(a => new
            {
                a.AppointmentId,
                a.AppointmentNo,
                a.TerminalId,
                a.Slot.StartAt,
                a.Slot.EndAt,
                Moves = a.Items.Select(i => i.ContainerNo ?? (i.CargoAllocation != null ? i.CargoAllocation.CargoRef : i.BookingRef)).ToList(),
            })
            .Take(500)
            .ToListAsync(ct);
        if (upcoming.Count == 0)
            return 0;

        var terminalIds = upcoming.Select(a => a.TerminalId).Distinct().ToList();
        var zones = await db.Terminals.AsNoTracking().Where(t => terminalIds.Contains(t.TerminalId))
            .ToDictionaryAsync(t => t.TerminalId, t => TimeZoneInfo.FindSystemTimeZoneById(t.TimeZone), ct);
        var settings = (await db.TerminalSettings.AsNoTracking().Where(s => terminalIds.Contains(s.TerminalId)).ToListAsync(ct))
            .GroupBy(s => s.TerminalId)
            .ToDictionary(g => g.Key, g => TerminalSettings.From(g));

        foreach (var a in upcoming)
        {
            var zone = zones[a.TerminalId];
            var early = settings.GetValueOrDefault(a.TerminalId, TerminalSettings.Defaults).EarlyGraceMinutes;
            var values = new Dictionary<string, string>
            {
                [NotificationText.Keys.AppointmentNo] = a.AppointmentNo,
                [NotificationText.Keys.Slot] = NotificationText.SlotLabel(a.StartAt, a.EndAt, zone),
                [NotificationText.Keys.GateOpens] = TimeZoneInfo.ConvertTime(a.StartAt.AddMinutes(-early), zone).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture),
                [NotificationText.Keys.Items] = string.Join(", ", a.Moves.Where(m => m is not null)),
            };
            await NotificationQueue.ForAppointmentAsync(db, a.AppointmentId, NotificationTemplates.SlotReminder, values,
                DriverChannel(o, a.TerminalId, await TerminalCodeAsync(a.TerminalId, ct)), now, ct, includeBooker: false, pushFirst: o.PushFirst);
        }

        await db.SaveChangesAsync(ct);
        return upcoming.Count;
    }

    private async Task<bool> AppointmentStillUpcomingAsync(long? appointmentId, CancellationToken ct) =>
        appointmentId is { } id && await db.Appointments.AsNoTracking().AnyAsync(a =>
            a.AppointmentId == id
            && (a.Status == AppointmentStatus.Confirmed || a.Status == AppointmentStatus.EnRoute)
            && a.Slot.EndAt > clock.GetUtcNow(), ct);

    /// <summary>Drivers: mobile. Users: email for EMAIL, mobile for SMS / WhatsApp.</summary>
    /// <summary>The terminal's driver channel (Terminals:&lt;code&gt;:Notifications:DriverChannel), else the installation's.</summary>
    private NotificationChannel DriverChannel(NotificationOptions o, int? terminalId, string? code = null)
    {
        code ??= terminalId is { } id ? TerminalProfiles.CachedCode(id) : null;
        return code is not null && profiles is not null && profiles.HasOwnNotifications(code) ? profiles.NotificationsFor(code).DriverChannel : o.DriverChannel;
    }

    private async Task<string?> TerminalCodeAsync(int? terminalId, CancellationToken ct) =>
        terminalId is { } id ? await TerminalProfiles.CodeAsync(db, id, ct) : null;

    private async Task<string?> RecipientAsync(NotificationLog n, CancellationToken ct)
    {
        if (n.DriverId is { } driverId)
        {
            if (n.Channel == NotificationChannel.Push)
                return await NotificationQueue.PushTokenAsync(db, driverId, ct);
            return n.Channel is NotificationChannel.Sms or NotificationChannel.Whatsapp
                ? await db.Drivers.AsNoTracking().Where(d => d.DriverId == driverId && !d.IsBlocked).Select(d => d.Mobile).SingleOrDefaultAsync(ct)
                : null;
        }

        if (n.UserId is { } userId)
        {
            var user = await db.AppUsers.AsNoTracking().Where(u => u.UserId == userId && u.IsActive)
                .Select(u => new { u.Email, u.Mobile }).SingleOrDefaultAsync(ct);
            return n.Channel == NotificationChannel.Email ? user?.Email : user?.Mobile;
        }

        return null;
    }

    private static IReadOnlyDictionary<string, string> Values(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new Dictionary<string, string>();
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>();
        }
    }

    private static string? Truncate(string? value, int max) =>
        value is null ? null : value.Length <= max ? value : value[..max];
}

/// <summary>Queues reminders and delivers the outbox every Notifications:IntervalSeconds (default 30).</summary>
public sealed class NotificationWorker(
    IServiceScopeFactory scopes, IOptionsMonitor<NotificationOptions> options, ILogger<NotificationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.CurrentValue.Enabled)
            return;

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(5, options.CurrentValue.IntervalSeconds)));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                var dispatcher = scope.ServiceProvider.GetRequiredService<NotificationDispatcher>();
                await dispatcher.QueueRemindersAsync(stoppingToken);
                await dispatcher.DispatchDueAsync(stoppingToken);
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
    }
}
