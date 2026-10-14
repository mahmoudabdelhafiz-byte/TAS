using Microsoft.EntityFrameworkCore;
using Tas.Domain;
using Tas.Infrastructure.Persistence;

namespace Tas.Infrastructure.Notifications;

/// <param name="RecipientType">Driver or User.</param>
public sealed record NotificationView(
    long NotificationId,
    string? AppointmentNo,
    string TemplateCode,
    NotificationChannel Channel,
    string Language,
    string RecipientType,
    string? RecipientName,
    string? Recipient,
    NotificationStatus Status,
    int Attempts,
    string? LastError,
    string? ProviderRef,
    DateTimeOffset CreatedAt,
    DateTimeOffset? NextAttemptAt,
    DateTimeOffset? SentAt);

public enum RetryStatus { Ok, NotFound, Conflict }

/// <summary>The outbox as the control room sees it: what was sent to whom, what failed and why; re-send failed ones.</summary>
public sealed class NotificationQueryService(TasDbContext db, TimeProvider clock)
{
    public const int MaxLimit = 500;

    public async Task<IReadOnlyList<NotificationView>> ListAsync(
        int terminalId, NotificationStatus? status, string? appointmentNo, int? limit, CancellationToken ct = default)
    {
        var query =
            from n in db.NotificationLogs.AsNoTracking()
            join a in db.Appointments.AsNoTracking() on n.AppointmentId equals (long?)a.AppointmentId
            where a.TerminalId == terminalId
            select new { n, a.AppointmentNo };

        if (status is { } s)
            query = query.Where(x => x.n.Status == s);
        if (!string.IsNullOrWhiteSpace(appointmentNo))
        {
            var no = appointmentNo.Trim().ToUpperInvariant();
            query = query.Where(x => x.AppointmentNo == no);
        }

        var rows = await query
            .OrderByDescending(x => x.n.NotificationId)
            .Take(Math.Clamp(limit ?? 100, 1, MaxLimit))
            .Select(x => new
            {
                x.n,
                x.AppointmentNo,
                DriverName = db.Drivers.Where(d => d.DriverId == x.n.DriverId).Select(d => d.FullName).FirstOrDefault(),
                UserName = db.AppUsers.Where(u => u.UserId == x.n.UserId).Select(u => u.FullName).FirstOrDefault(),
            })
            .ToListAsync(ct);

        return rows.Select(r => new NotificationView(
            r.n.NotificationId, r.AppointmentNo, r.n.TemplateCode, r.n.Channel, r.n.Language.Trim(),
            r.n.DriverId is not null ? "Driver" : "User",
            r.n.DriverId is not null ? r.DriverName : r.UserName,
            r.n.Recipient, r.n.Status, r.n.Attempts, r.n.LastError, r.n.ProviderRef,
            r.n.CreatedAt, r.n.NextAttemptAt, r.n.SentAt)).ToList();
    }

    /// <summary>
    /// Puts a FAILED or SKIPPED message back in the queue (e.g. after fixing a driver's mobile number). It is
    /// checked again before sending, so a message that no longer applies is skipped again.
    /// </summary>
    public async Task<(RetryStatus Status, string? Error)> RetryAsync(int terminalId, long notificationId, CancellationToken ct = default)
    {
        var n = await (
                from x in db.NotificationLogs
                join a in db.Appointments on x.AppointmentId equals (long?)a.AppointmentId
                where x.NotificationId == notificationId && a.TerminalId == terminalId
                select x)
            .SingleOrDefaultAsync(ct);
        if (n is null)
            return (RetryStatus.NotFound, $"Notification {notificationId} not found.");
        if (n.Status is not (NotificationStatus.Failed or NotificationStatus.Skipped))
            return (RetryStatus.Conflict, $"Only failed or skipped notifications can be sent again (this one is {n.Status}).");

        n.Status = NotificationStatus.Queued;
        n.Attempts = 0;
        n.NextAttemptAt = null;
        n.LastError = null;
        n.CreatedAt = clock.GetUtcNow(); // counts as a new message for the "too old" rule
        await db.SaveChangesAsync(ct);
        return (RetryStatus.Ok, null);
    }
}
