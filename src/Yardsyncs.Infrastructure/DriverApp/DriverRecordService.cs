using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Yardsyncs.Application.Auth;
using Yardsyncs.Application.Capacity;
using Yardsyncs.Application.MasterData;
using Yardsyncs.Application.Notifications;
using Yardsyncs.Domain;
using Yardsyncs.Domain.Gate;
using Yardsyncs.Domain.Parties;
using Yardsyncs.Infrastructure.Notifications;
using Yardsyncs.Infrastructure.Persistence;

namespace Yardsyncs.Infrastructure.DriverApp;

/// <summary>An entry as the driver sees it in the app (no staff names).</summary>
public sealed record DriverIncidentView(
    long IncidentId, string TerminalName, string? AppointmentNo, IncidentKind Kind, int Points, DateTimeOffset OccurredAt, string? Note,
    DisputeStatus? DisputeStatus, string? DisputeText, string? DisputeAnswer, bool Withdrawn, bool Counts, DateTimeOffset CountsUntil, bool CanDispute);

/// <summary>The driver's own record in the app: points per terminal and the entries.</summary>
public sealed record MyRecordView(IReadOnlyList<MyTerminalPoints> Terminals, IReadOnlyList<DriverIncidentView> Incidents);

public sealed record MyTerminalPoints(int TerminalId, string TerminalName, int Points, DriverStanding Standing);

/// <summary>
/// The driver record: penalties and misbehaviour at a terminal with points. A driver is one person across companies
/// (every driver record with the same mobile number), so points follow the person; a company sees only the entries on
/// its own drivers (when the terminal allows it). Disputed entries are decided by planners and terminal admins.
/// </summary>
public sealed class DriverRecordService(YardsyncsDbContext db, TimeProvider clock, IConfiguration config)
{
    // ================================================================ terminal and companies

    /// <summary>Drivers with points that count, or an open dispute, most points first. A company gets only its drivers.</summary>
    public async Task<IReadOnlyList<DriverPointsRow>> ListAsync(int terminalId, int? companyId, string? search, CancellationToken ct = default)
    {
        var settings = await SettingsAsync(terminalId, ct);
        var now = clock.GetUtcNow();
        var since = now.AddDays(-Math.Max(settings.DriverPointsDays, 90));
        var rows = await db.DriverIncidents.AsNoTracking()
            .Where(i => i.TerminalId == terminalId && (i.OccurredAt >= since || i.DisputeStatus == DisputeStatus.Open))
            .Join(db.Drivers, i => i.DriverId, d => d.DriverId, (i, d) => new { i, d.CompanyId, d.Mobile })
            .Where(x => companyId == null || x.CompanyId == companyId)
            .ToListAsync(ct);

        // Points per person (mobile); the row shows the driver record most recently involved.
        var people = rows.GroupBy(x => x.Mobile).ToList();
        var driverIds = rows.Select(x => x.i.DriverId).Distinct().ToList();
        var drivers = await db.Drivers.AsNoTracking().Where(d => driverIds.Contains(d.DriverId))
            .Select(d => new { d.DriverId, d.FullName, d.Mobile, Company = db.Companies.Where(c => c.CompanyId == d.CompanyId).Select(c => c.Name).FirstOrDefault() })
            .ToDictionaryAsync(d => d.DriverId, ct);

        var result = new List<DriverPointsRow>();
        foreach (var person in people)
        {
            var points = person.Where(x => DriverRecordRules.Counts(x.i.OccurredAt, x.i.WithdrawnAt, x.i.DisputeStatus, settings.DriverPointsDays, now)).Sum(x => x.i.Points);
            var open = person.Count(x => x.i.DisputeStatus == DisputeStatus.Open && x.i.WithdrawnAt is null);
            if (points == 0 && open == 0)
                continue;
            var latest = person.OrderByDescending(x => x.i.OccurredAt).First();
            var d = drivers[latest.i.DriverId];
            result.Add(new DriverPointsRow(d.DriverId, d.FullName, d.Mobile, d.Company ?? "", points, DriverRecordRules.Standing(points, settings), open, latest.i.OccurredAt));
        }

        var term = search?.Trim();
        return result
            .Where(r => string.IsNullOrEmpty(term) || r.FullName.Contains(term, StringComparison.OrdinalIgnoreCase) || r.Mobile.Contains(term))
            .OrderByDescending(r => r.Open > 0).ThenByDescending(r => r.Points).ThenBy(r => r.FullName, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>One driver's record at the terminal; a company sees the entries on its own drivers only.</summary>
    public async Task<DriverRecordView?> RecordAsync(int terminalId, int driverId, int? companyId, CancellationToken ct = default)
    {
        var driver = await db.Drivers.AsNoTracking().Where(d => d.DriverId == driverId)
            .Select(d => new { d.DriverId, d.FullName, d.Mobile, d.CompanyId, Company = db.Companies.Where(c => c.CompanyId == d.CompanyId).Select(c => c.Name).FirstOrDefault() })
            .SingleOrDefaultAsync(ct);
        if (driver is null || (companyId is not null && driver.CompanyId != companyId))
            return null;
        var settings = await SettingsAsync(terminalId, ct);
        var now = clock.GetUtcNow();

        var rows = await db.DriverIncidents.AsNoTracking()
            .Where(i => i.TerminalId == terminalId
                        && db.Drivers.Any(d => d.DriverId == i.DriverId && d.Mobile == driver.Mobile && (companyId == null || d.CompanyId == companyId)))
            .OrderByDescending(i => i.OccurredAt)
            .Select(i => new
            {
                i,
                AppointmentNo = db.Appointments.Where(a => a.AppointmentId == i.AppointmentId).Select(a => a.AppointmentNo).FirstOrDefault(),
                RecordedBy = db.AppUsers.Where(u => u.UserId == i.RecordedBy).Select(u => u.FullName).FirstOrDefault(),
            })
            .ToListAsync(ct);
        var incidents = rows.Select(r => View(r.i, r.AppointmentNo, companyId is null ? r.RecordedBy : null, settings, now)).ToList();
        var points = incidents.Where(i => i.Counts).Sum(i => i.Points);
        return new DriverRecordView(driver.DriverId, driver.FullName, driver.Mobile, driver.Company ?? "", points,
            DriverRecordRules.Standing(points, settings), settings.DriverWarnPoints, settings.DriverLimitPoints, settings.DriverPointsDays, incidents);
    }

    public async Task<AdminResult<DriverRecordView>> AddAsync(CurrentUser actor, int terminalId, int driverId, IncidentInput input, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        if (DriverRecordRules.Check(input, now) is { } error)
            return AdminResult<DriverRecordView>.Invalid(error);
        if (!await db.Drivers.AnyAsync(d => d.DriverId == driverId, ct))
            return AdminResult<DriverRecordView>.NotFound($"Driver {driverId} not found.");

        long? appointmentId = null;
        if (!string.IsNullOrWhiteSpace(input.AppointmentNo))
        {
            var number = input.AppointmentNo.Trim().ToUpperInvariant();
            appointmentId = await db.Appointments.Where(a => a.TerminalId == terminalId && a.AppointmentNo == number)
                .Select(a => (long?)a.AppointmentId).SingleOrDefaultAsync(ct);
            if (appointmentId is null)
                return AdminResult<DriverRecordView>.Invalid($"Appointment {number} was not found.");
        }

        var incident = new DriverIncident
        {
            TerminalId = terminalId,
            DriverId = driverId,
            AppointmentId = appointmentId,
            Kind = input.Kind,
            Points = (short)(input.Points ?? DriverRecordRules.DefaultPoints(input.Kind)),
            OccurredAt = input.OccurredAt ?? now,
            Note = string.IsNullOrWhiteSpace(input.Note) ? null : input.Note.Trim(),
            RecordedBy = actor.UserId,
            RecordedAt = now,
        };
        db.DriverIncidents.Add(incident);

        // The driver is told (in their language) on their phone when the entry belongs to a visit.
        if (appointmentId is { } visit)
        {
            var values = new Dictionary<string, string>
            {
                [NotificationText.Keys.AppointmentNo] = input.AppointmentNo!.Trim().ToUpperInvariant(),
                [NotificationText.Keys.Reason] = KindText(input.Kind),
            };
            await NotificationQueue.ForAppointmentAsync(db, visit, NotificationTemplates.DriverIncident, values,
                await NotificationQueue.DriverChannelAsync(db, config, terminalId, ct), now, ct, includeBooker: false, pushFirst: NotificationQueue.PushFirst(config));
        }
        await db.SaveChangesAsync(ct);
        return AdminResult<DriverRecordView>.Created((await RecordAsync(terminalId, driverId, null, ct))!);
    }

    public async Task<AdminResult<DriverRecordView>> WithdrawAsync(CurrentUser actor, int terminalId, long incidentId, string? reason, CancellationToken ct = default)
    {
        var why = reason?.Trim() ?? "";
        if (why.Length is 0 or > 300)
            return AdminResult<DriverRecordView>.Invalid("Say why the entry is withdrawn (at most 300 characters).");
        var incident = await db.DriverIncidents.SingleOrDefaultAsync(i => i.IncidentId == incidentId && i.TerminalId == terminalId, ct);
        if (incident is null)
            return AdminResult<DriverRecordView>.NotFound($"Entry {incidentId} not found.");
        if (incident.WithdrawnAt is not null)
            return AdminResult<DriverRecordView>.Conflict("The entry was already withdrawn.");
        incident.WithdrawnAt = clock.GetUtcNow();
        incident.WithdrawnBy = actor.UserId;
        incident.WithdrawReason = why;
        await db.SaveChangesAsync(ct);
        return AdminResult<DriverRecordView>.Ok((await RecordAsync(terminalId, incident.DriverId, null, ct))!);
    }

    /// <summary>The terminal's answer to a dispute: accepted takes the points off; either way the driver sees the answer.</summary>
    public async Task<AdminResult<DriverRecordView>> DecideAsync(CurrentUser actor, int terminalId, long incidentId, DisputeDecisionInput input, CancellationToken ct = default)
    {
        var answer = input.Answer?.Trim() ?? "";
        if (answer.Length is 0 or > 500)
            return AdminResult<DriverRecordView>.Invalid("Write the answer the driver will see (at most 500 characters).");
        var incident = await db.DriverIncidents.SingleOrDefaultAsync(i => i.IncidentId == incidentId && i.TerminalId == terminalId, ct);
        if (incident is null)
            return AdminResult<DriverRecordView>.NotFound($"Entry {incidentId} not found.");
        if (incident.DisputeStatus != DisputeStatus.Open)
            return AdminResult<DriverRecordView>.Conflict("There is no open dispute on this entry.");
        incident.DisputeStatus = input.Accept ? DisputeStatus.Accepted : DisputeStatus.Rejected;
        incident.DisputeAnswer = answer;
        incident.DecidedBy = actor.UserId;
        incident.DecidedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return AdminResult<DriverRecordView>.Ok((await RecordAsync(terminalId, incident.DriverId, null, ct))!);
    }

    // ================================================================ the driver (app)

    public async Task<MyRecordView> ForDriverAsync(CurrentDriver driver, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var rows = await db.DriverIncidents.AsNoTracking()
            .Where(i => db.Drivers.Any(d => d.DriverId == i.DriverId && d.Mobile == driver.Mobile))
            .OrderByDescending(i => i.OccurredAt)
            .Select(i => new
            {
                i,
                Terminal = db.Terminals.Where(t => t.TerminalId == i.TerminalId).Select(t => t.Name).FirstOrDefault(),
                AppointmentNo = db.Appointments.Where(a => a.AppointmentId == i.AppointmentId).Select(a => a.AppointmentNo).FirstOrDefault(),
            })
            .Take(200)
            .ToListAsync(ct);

        var settingsByTerminal = new Dictionary<int, TerminalSettings>();
        foreach (var terminalId in rows.Select(r => r.i.TerminalId).Distinct())
            settingsByTerminal[terminalId] = await SettingsAsync(terminalId, ct);

        var incidents = rows.Select(r =>
        {
            var settings = settingsByTerminal[r.i.TerminalId];
            var view = View(r.i, r.AppointmentNo, null, settings, now);
            return new DriverIncidentView(r.i.IncidentId, r.Terminal ?? "", r.AppointmentNo, r.i.Kind, r.i.Points, r.i.OccurredAt, r.i.Note,
                r.i.DisputeStatus, r.i.DisputeText, r.i.DisputeAnswer, r.i.WithdrawnAt is not null, view.Counts, view.CountsUntil,
                CanDispute(r.i, settings, now));
        }).ToList();
        var terminals = rows.GroupBy(r => (r.i.TerminalId, r.Terminal))
            .Select(g =>
            {
                var settings = settingsByTerminal[g.Key.TerminalId];
                var points = incidents.Where(i => i.Counts && g.Any(r => r.i.IncidentId == i.IncidentId)).Sum(i => i.Points);
                return new MyTerminalPoints(g.Key.TerminalId, g.Key.Terminal ?? "", points, DriverRecordRules.Standing(points, settings));
            })
            .ToList();
        return new MyRecordView(terminals, incidents);
    }

    /// <summary>The driver disputes an entry once, while it counts, saying why.</summary>
    public async Task<AdminResult<MyRecordView>> DisputeAsync(CurrentDriver driver, long incidentId, DisputeInput input, CancellationToken ct = default)
    {
        var text = input.Text?.Trim() ?? "";
        if (text.Length is 0 or > 500)
            return AdminResult<MyRecordView>.Invalid("Say why you dispute it (at most 500 characters).");
        var incident = await db.DriverIncidents
            .SingleOrDefaultAsync(i => i.IncidentId == incidentId && db.Drivers.Any(d => d.DriverId == i.DriverId && d.Mobile == driver.Mobile), ct);
        if (incident is null)
            return AdminResult<MyRecordView>.NotFound($"Entry {incidentId} not found.");
        var now = clock.GetUtcNow();
        if (!CanDispute(incident, await SettingsAsync(incident.TerminalId, ct), now))
            return AdminResult<MyRecordView>.Conflict("This entry can no longer be disputed.");
        incident.DisputeStatus = DisputeStatus.Open;
        incident.DisputeText = text;
        incident.DisputedAt = now;
        await db.SaveChangesAsync(ct);
        return AdminResult<MyRecordView>.Ok(await ForDriverAsync(driver, ct));
    }

    // ================================================================ the gate and the control room

    /// <summary>Points that count now for the person behind a driver record at a terminal.</summary>
    public static async Task<int> PointsAsync(YardsyncsDbContext db, int terminalId, int driverId, TerminalSettings settings, DateTimeOffset now, CancellationToken ct)
    {
        var since = now.AddDays(-settings.DriverPointsDays);
        var mobile = await db.Drivers.AsNoTracking().Where(d => d.DriverId == driverId).Select(d => d.Mobile).SingleOrDefaultAsync(ct);
        if (mobile is null)
            return 0;
        return await db.DriverIncidents.AsNoTracking()
            .Where(i => i.TerminalId == terminalId && i.OccurredAt > since && i.WithdrawnAt == null
                        && (i.DisputeStatus == null || i.DisputeStatus != DisputeStatus.Accepted)
                        && db.Drivers.Any(d => d.DriverId == i.DriverId && d.Mobile == mobile))
            .SumAsync(i => (int)i.Points, ct);
    }

    /// <summary>Points per mobile number at a terminal, for many drivers at once (the control room).</summary>
    public static async Task<Dictionary<string, int>> PointsByMobileAsync(
        YardsyncsDbContext db, int terminalId, IReadOnlyCollection<string> mobiles, TerminalSettings settings, DateTimeOffset now, CancellationToken ct)
    {
        if (mobiles.Count == 0)
            return new Dictionary<string, int>();
        var since = now.AddDays(-settings.DriverPointsDays);
        return (await db.DriverIncidents.AsNoTracking()
                .Where(i => i.TerminalId == terminalId && i.OccurredAt > since && i.WithdrawnAt == null
                            && (i.DisputeStatus == null || i.DisputeStatus != DisputeStatus.Accepted))
                .Join(db.Drivers.Where(d => mobiles.Contains(d.Mobile)), i => i.DriverId, d => d.DriverId, (i, d) => new { d.Mobile, i.Points })
                .ToListAsync(ct))
            .GroupBy(x => x.Mobile)
            .ToDictionary(g => g.Key, g => g.Sum(x => (int)x.Points));
    }

    // ================================================================ helpers

    /// <summary>Whether trucking companies see their own drivers' records at this terminal.</summary>
    public async Task<bool> CompanySeesAsync(int terminalId, CancellationToken ct = default) => (await SettingsAsync(terminalId, ct)).DriverRecordToCompany;

    private static bool CanDispute(DriverIncident i, TerminalSettings settings, DateTimeOffset now) =>
        i.DisputeStatus is null && DriverRecordRules.Counts(i.OccurredAt, i.WithdrawnAt, i.DisputeStatus, settings.DriverPointsDays, now);

    private static IncidentView View(DriverIncident i, string? appointmentNo, string? recordedBy, TerminalSettings settings, DateTimeOffset now) =>
        new(i.IncidentId, i.TerminalId, i.DriverId, appointmentNo, i.Kind, i.Points, i.OccurredAt, i.Note, recordedBy, i.RecordedAt,
            i.DisputeStatus, i.DisputeText, i.DisputedAt, i.DisputeAnswer, i.DecidedAt, i.WithdrawnAt, i.WithdrawReason,
            DriverRecordRules.Counts(i.OccurredAt, i.WithdrawnAt, i.DisputeStatus, settings.DriverPointsDays, now),
            i.OccurredAt.AddDays(settings.DriverPointsDays));

    /// <summary>The kind in English words, for the driver's message (translated into their language when sent).</summary>
    public static string KindText(IncidentKind kind) => kind switch
    {
        IncidentKind.Speeding => "Speeding",
        IncidentKind.UnsafeDriving => "Unsafe driving",
        IncidentKind.NoPpe => "No safety equipment",
        IncidentKind.WrongArea => "In a wrong area",
        IncidentKind.Damage => "Damage",
        IncidentKind.Abusive => "Abusive behaviour",
        IncidentKind.NoShow => "No-show",
        _ => "Other",
    };

    private async Task<TerminalSettings> SettingsAsync(int terminalId, CancellationToken ct) =>
        TerminalSettings.From(await db.TerminalSettings.AsNoTracking().Where(s => s.TerminalId == terminalId).ToListAsync(ct));
}
