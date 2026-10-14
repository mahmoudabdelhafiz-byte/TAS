using Microsoft.EntityFrameworkCore;
using Tas.Domain;
using Tas.Infrastructure.Persistence;

namespace Tas.Infrastructure.Notifications;

/// <summary>
/// Queues notifications about an appointment in the outbox (added to the context; the caller saves, so the
/// message is only queued if the change that caused it is committed). The sender delivers them later.
/// </summary>
public static class NotificationOutbox
{
    /// <summary>
    /// SMS to the assigned driver (in the driver's language) and email to the person who booked (if still active).
    /// Returns how many messages were queued.
    /// </summary>
    public static async Task<int> QueueAsync(
        TasDbContext db,
        long appointmentId,
        int? driverId,
        int bookerUserId,
        string template,
        DateTimeOffset now,
        CancellationToken ct,
        bool toDriver = true,
        bool toBooker = true)
    {
        var queued = 0;

        if (toDriver && driverId is { } id)
        {
            var language = await db.Drivers.AsNoTracking().Where(d => d.DriverId == id).Select(d => d.PreferredLanguage).SingleOrDefaultAsync(ct);
            if (language is not null)
            {
                db.NotificationLogs.Add(new NotificationLog
                {
                    AppointmentId = appointmentId,
                    DriverId = id,
                    Channel = NotificationChannel.Sms,
                    TemplateCode = template,
                    Language = language.Trim(),
                    CreatedAt = now,
                });
                queued++;
            }
        }

        if (toBooker)
        {
            var booker = await db.AppUsers.AsNoTracking()
                .Where(u => u.UserId == bookerUserId && u.IsActive)
                .Select(u => new { u.UserId, u.PreferredLanguage })
                .SingleOrDefaultAsync(ct);
            if (booker is not null)
            {
                db.NotificationLogs.Add(new NotificationLog
                {
                    AppointmentId = appointmentId,
                    UserId = booker.UserId,
                    Channel = NotificationChannel.Email,
                    TemplateCode = template,
                    Language = booker.PreferredLanguage.Trim(),
                    CreatedAt = now,
                });
                queued++;
            }
        }

        return queued;
    }
}
