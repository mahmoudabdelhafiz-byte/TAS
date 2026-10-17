using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Yardsyncs.Application.Auth;
using Yardsyncs.Application.Capacity;
using Yardsyncs.Application.Languages;
using Yardsyncs.Application.Messages;
using Yardsyncs.Application.Notifications;
using Yardsyncs.Domain;
using Yardsyncs.Domain.Booking;
using Yardsyncs.Infrastructure.Notifications;
using Yardsyncs.Infrastructure.Persistence;

namespace Yardsyncs.Infrastructure.Messages;

/// <summary>
/// Messages between a visit's driver and the terminal's control room: ready-made ones (shown in each reader's language)
/// or text. A message to the driver also goes out as a push (else by the driver channel), so a driver without the app
/// open still sees it. Reading a conversation marks the other side's messages read.
/// </summary>
public sealed class MessageService(YardsyncsDbContext db, TimeProvider clock, IConfiguration config)
{
    /// <summary>How many visits the control room's list shows (latest message first).</summary>
    public const int ListLimit = 100;

    // ================================================================ driver

    public async Task<AdminResult<IReadOnlyList<MessageView>>> ForDriverAsync(CurrentDriver driver, string appointmentNo, CancellationToken ct = default)
    {
        var visit = await DriversVisitAsync(driver, appointmentNo, ct);
        if (visit is null)
            return AdminResult<IReadOnlyList<MessageView>>.NotFound($"Appointment {Number(appointmentNo)} not found.");
        await MarkReadAsync(visit.AppointmentId, MessageDirection.ToDriver, ct);
        // The driver writes to "the control room", not to a person.
        return AdminResult<IReadOnlyList<MessageView>>.Ok((await ThreadAsync(visit.AppointmentId, ct)).Select(m => m with { SenderName = null }).ToList());
    }

    public async Task<AdminResult<MessageView>> FromDriverAsync(CurrentDriver driver, string appointmentNo, MessageInput input, CancellationToken ct = default)
    {
        var visit = await DriversVisitAsync(driver, appointmentNo, ct);
        if (visit is null)
            return AdminResult<MessageView>.NotFound($"Appointment {Number(appointmentNo)} not found.");
        return await SendAsync(visit, MessageDirection.FromDriver, null, input, ct);
    }

    // ================================================================ control room

    /// <summary>Visits with messages at the terminal (live, or within a day of their slot), unread and latest first.</summary>
    public async Task<IReadOnlyList<ConversationView>> ConversationsAsync(int terminalId, CancellationToken ct = default)
    {
        var since = clock.GetUtcNow() - MessageRules.AfterSlot - TimeSpan.FromHours(24);
        var rows = await db.AppointmentMessages.AsNoTracking()
            .Where(m => m.AppointmentId != null && db.Appointments.Any(a => a.AppointmentId == m.AppointmentId && a.TerminalId == terminalId))
            .Where(m => m.SentAt >= since)
            .GroupBy(m => m.AppointmentId!.Value)
            .Select(g => new
            {
                AppointmentId = g.Key,
                LastAt = g.Max(m => m.SentAt),
                LastId = g.Max(m => m.MessageId),
                Unread = g.Count(m => m.Direction == MessageDirection.FromDriver && m.ReadAt == null),
            })
            .ToListAsync(ct);
        rows = rows.OrderByDescending(g => g.Unread > 0).ThenByDescending(g => g.LastAt).Take(ListLimit).ToList();
        if (rows.Count == 0)
            return [];

        var ids = rows.Select(r => r.AppointmentId).ToList();
        var visits = await db.Appointments.AsNoTracking()
            .Where(a => ids.Contains(a.AppointmentId))
            .Select(a => new
            {
                a.AppointmentId, a.AppointmentNo, a.Status, a.Slot.StartAt, Company = a.Company.Name,
                Plate = a.Truck != null ? a.Truck.PlateNumber : null,
                Driver = a.Driver != null ? a.Driver.FullName : null,
                Mobile = a.Driver != null ? a.Driver.Mobile : null,
            })
            .ToDictionaryAsync(a => a.AppointmentId, ct);
        var lastIds = rows.Select(r => r.LastId).ToList();
        var last = await db.AppointmentMessages.AsNoTracking().Where(m => lastIds.Contains(m.MessageId)).ToListAsync(ct);
        var senders = await SenderNamesAsync(last, ct);
        var lastById = last.ToDictionary(m => m.AppointmentId!.Value);

        return rows.Where(r => visits.ContainsKey(r.AppointmentId) && lastById.ContainsKey(r.AppointmentId)).Select(r =>
        {
            var v = visits[r.AppointmentId];
            return new ConversationView(v.AppointmentNo, v.Status, v.StartAt, v.Company, v.Plate, v.Driver, v.Mobile, r.Unread,
                View(lastById[r.AppointmentId], senders));
        }).ToList();
    }

    public async Task<AdminResult<IReadOnlyList<MessageView>>> ForControlRoomAsync(int terminalId, string appointmentNo, CancellationToken ct = default)
    {
        var visit = await TerminalVisitAsync(terminalId, appointmentNo, ct);
        if (visit is null)
            return AdminResult<IReadOnlyList<MessageView>>.NotFound($"Appointment {Number(appointmentNo)} not found.");
        await MarkReadAsync(visit.AppointmentId, MessageDirection.FromDriver, ct);
        return AdminResult<IReadOnlyList<MessageView>>.Ok(await ThreadAsync(visit.AppointmentId, ct));
    }

    public async Task<AdminResult<MessageView>> ToDriverAsync(CurrentUser user, int terminalId, string appointmentNo, MessageInput input, CancellationToken ct = default)
    {
        var visit = await TerminalVisitAsync(terminalId, appointmentNo, ct);
        if (visit is null)
            return AdminResult<MessageView>.NotFound($"Appointment {Number(appointmentNo)} not found.");
        return await SendAsync(visit, MessageDirection.ToDriver, user, input, ct);
    }

    // ================================================================ shared

    private sealed record Visit(long AppointmentId, string AppointmentNo, int TerminalId, int? DriverId, AppointmentStatus Status, DateTimeOffset SlotEnd);

    private async Task<AdminResult<MessageView>> SendAsync(Visit visit, MessageDirection direction, CurrentUser? sender, MessageInput input, CancellationToken ct)
    {
        if (visit.DriverId is not { } driverId)
            return AdminResult<MessageView>.Conflict($"Appointment {visit.AppointmentNo} names no driver to write to.");
        var now = clock.GetUtcNow();
        if (!MessageRules.IsOpen(visit.Status, visit.SlotEnd, now))
            return AdminResult<MessageView>.Conflict($"Appointment {visit.AppointmentNo} is closed for messages.");
        var (error, type, templateCode, body) = MessageRules.Check(direction, input);
        if (error is not null)
            return AdminResult<MessageView>.Invalid(error);

        var message = new AppointmentMessage
        {
            AppointmentId = visit.AppointmentId,
            DriverId = driverId,
            Direction = direction,
            SenderUserId = direction == MessageDirection.ToDriver ? sender?.UserId : null,
            MessageType = type,
            TemplateCode = templateCode,
            Body = body,
            SentAt = now,
        };
        db.AppointmentMessages.Add(message);

        if (direction == MessageDirection.ToDriver)
        {
            // The driver's phone gets it too, in their language.
            var language = await db.Drivers.AsNoTracking().Where(d => d.DriverId == driverId).Select(d => d.PreferredLanguage).SingleOrDefaultAsync(ct);
            var text = MessageRules.TextFor(templateCode, body, language);
            var values = new Dictionary<string, string>
            {
                [NotificationText.Keys.AppointmentNo] = visit.AppointmentNo,
                [NotificationText.Keys.Message] = text.Length > 300 ? text[..297] + "..." : text,
            };
            await NotificationQueue.ForAppointmentAsync(db, visit.AppointmentId, NotificationTemplates.ControlRoomMessage, values,
                await NotificationQueue.DriverChannelAsync(db, config, visit.TerminalId, ct), now, ct,
                includeBooker: false, pushFirst: NotificationQueue.PushFirst(config));
        }

        await db.SaveChangesAsync(ct);
        var senders = await SenderNamesAsync([message], ct);
        return AdminResult<MessageView>.Created(View(message, senders));
    }

    private async Task<IReadOnlyList<MessageView>> ThreadAsync(long appointmentId, CancellationToken ct)
    {
        var messages = await db.AppointmentMessages.AsNoTracking()
            .Where(m => m.AppointmentId == appointmentId)
            .OrderBy(m => m.MessageId)
            .ToListAsync(ct);
        var senders = await SenderNamesAsync(messages, ct);
        return messages.Select(m => View(m, senders)).ToList();
    }

    private Task MarkReadAsync(long appointmentId, MessageDirection direction, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        return db.AppointmentMessages
            .Where(m => m.AppointmentId == appointmentId && m.Direction == direction && m.ReadAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(m => m.ReadAt, now), ct);
    }

    private async Task<Dictionary<int, string>> SenderNamesAsync(IEnumerable<AppointmentMessage> messages, CancellationToken ct)
    {
        var ids = messages.Where(m => m.SenderUserId is not null).Select(m => m.SenderUserId!.Value).Distinct().ToList();
        return ids.Count == 0
            ? new Dictionary<int, string>()
            : await db.AppUsers.AsNoTracking().Where(u => ids.Contains(u.UserId)).ToDictionaryAsync(u => u.UserId, u => u.FullName, ct);
    }

    /// <summary>Ready-made messages in the reader's language (the request's); the sender's name only for the control room's side.</summary>
    private static MessageView View(AppointmentMessage m, IReadOnlyDictionary<int, string> senders) =>
        new(m.MessageId, m.Direction, m.MessageType, m.TemplateCode, MessageRules.TextFor(m.MessageType, m.TemplateCode, m.Body, ServerText.Language),
            m.SenderUserId is { } id ? senders.GetValueOrDefault(id) : null, m.SentAt, m.ReadAt, MessageRules.PointOf(m.MessageType, m.Body));

    private Task<Visit?> DriversVisitAsync(CurrentDriver driver, string appointmentNo, CancellationToken ct)
    {
        var number = Number(appointmentNo);
        return db.Appointments.AsNoTracking()
            .Where(a => a.AppointmentNo == number && a.DriverId != null
                        && db.Drivers.Any(d => d.DriverId == a.DriverId && d.Mobile == driver.Mobile && !d.IsBlocked))
            .Select(a => new Visit(a.AppointmentId, a.AppointmentNo, a.TerminalId, a.DriverId, a.Status, a.Slot.EndAt))
            .FirstOrDefaultAsync(ct);
    }

    private Task<Visit?> TerminalVisitAsync(int terminalId, string appointmentNo, CancellationToken ct)
    {
        var number = Number(appointmentNo);
        return db.Appointments.AsNoTracking()
            .Where(a => a.TerminalId == terminalId && a.AppointmentNo == number)
            .Select(a => new Visit(a.AppointmentId, a.AppointmentNo, a.TerminalId, a.DriverId, a.Status, a.Slot.EndAt))
            .SingleOrDefaultAsync(ct);
    }

    private static string Number(string appointmentNo) => appointmentNo.Trim().ToUpperInvariant();
}
