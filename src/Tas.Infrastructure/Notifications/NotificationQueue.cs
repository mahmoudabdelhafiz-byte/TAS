using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Tas.Domain;
using Tas.Infrastructure.Persistence;

namespace Tas.Infrastructure.Notifications;

/// <summary>
/// Puts notifications for an appointment into the outbox (notification_log, status QUEUED); the dispatcher
/// renders and sends them. The rows are added to the context and saved with the caller's change.
/// </summary>
public static class NotificationQueue
{
    /// <summary>"Notifications:DriverChannel": Sms (default) or Whatsapp.</summary>
    public static NotificationChannel DriverChannel(IConfiguration? config) =>
        config?.GetValue("Notifications:DriverChannel", NotificationChannel.Sms) ?? NotificationChannel.Sms;

    /// <summary>Push first for drivers with the app: a push provider is set and "Notifications:DriverPush" is not off.</summary>
    public static bool PushFirst(IConfiguration? config)
    {
        var o = new NotificationOptions();
        config?.GetSection("Notifications").Bind(o);
        return o.PushFirst;
    }

    /// <summary>The push token of the app the driver (any of their driver records, by mobile) is signed in to.</summary>
    public static Task<string?> PushTokenAsync(TasDbContext db, int driverId, CancellationToken ct) =>
        db.DriverDevices.AsNoTracking()
            .Where(d => d.IsActive && d.PushToken != null
                        && db.Drivers.Any(x => x.DriverId == d.DriverId
                                               && x.Mobile == db.Drivers.Where(y => y.DriverId == driverId).Select(y => y.Mobile).FirstOrDefault()))
            .OrderByDescending(d => d.LastSeenAt)
            .Select(d => d.PushToken)
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// Queues <paramref name="template"/> for the appointment's driver (if assigned and not blocked) on
    /// <paramref name="driverChannel"/>, and for the user who booked (if active) by email.
    /// </summary>
    public static async Task<int> ForAppointmentAsync(
        TasDbContext db,
        long appointmentId,
        string template,
        IReadOnlyDictionary<string, string> values,
        NotificationChannel driverChannel,
        DateTimeOffset now,
        CancellationToken ct,
        bool includeBooker = true,
        bool pushFirst = false)
    {
        var target = await db.Appointments.AsNoTracking()
            .Where(a => a.AppointmentId == appointmentId)
            .Select(a => new
            {
                a.TerminalId,
                a.DriverId,
                DriverLanguage = a.Driver != null ? a.Driver.PreferredLanguage : null,
                DriverBlocked = a.Driver != null && a.Driver.IsBlocked,
                a.CreatedBy,
            })
            .SingleOrDefaultAsync(ct);
        if (target is null)
            return 0;

        var json = JsonSerializer.Serialize(values);
        var queued = 0;

        if (target.DriverId is { } driverId && !target.DriverBlocked)
        {
            var channel = pushFirst && await PushTokenAsync(db, driverId, ct) is not null ? NotificationChannel.Push : driverChannel;
            db.NotificationLogs.Add(New(appointmentId, target.TerminalId, template, channel, target.DriverLanguage, json, now, driverId: driverId));
            queued++;
        }

        if (!includeBooker)
            return queued;

        var booker = await db.AppUsers.AsNoTracking()
            .Where(u => u.UserId == target.CreatedBy && u.IsActive)
            .Select(u => new { u.UserId, u.PreferredLanguage })
            .SingleOrDefaultAsync(ct);
        if (booker is not null)
        {
            db.NotificationLogs.Add(New(appointmentId, target.TerminalId, template, NotificationChannel.Email, booker.PreferredLanguage, json, now, userId: booker.UserId));
            queued++;
        }

        return queued;
    }

    private static NotificationLog New(
        long appointmentId, int terminalId, string template, NotificationChannel channel, string? language, string json, DateTimeOffset now,
        int? driverId = null, int? userId = null) =>
        new()
        {
            AppointmentId = appointmentId,
            TerminalId = terminalId,
            DriverId = driverId,
            UserId = userId,
            Channel = channel,
            TemplateCode = template,
            Language = string.IsNullOrWhiteSpace(language) ? "en" : language.Trim().ToLowerInvariant(),
            Status = NotificationStatus.Queued,
            Params = json,
            CreatedAt = now,
        };
}
