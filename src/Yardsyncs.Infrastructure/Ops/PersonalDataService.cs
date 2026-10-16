using Microsoft.EntityFrameworkCore;
using Yardsyncs.Application.Auth;
using Yardsyncs.Application.Capacity;
using Yardsyncs.Domain;
using Yardsyncs.Domain.Booking;
using Yardsyncs.Infrastructure.Persistence;

namespace Yardsyncs.Infrastructure.Ops;

public sealed record PersonalDriver(int DriverId, int CompanyId, string Company, string FullName, string IdNumber, string Mobile, string PreferredLanguage, bool IsBlocked);
public sealed record PersonalDevice(string Platform, string? AppVersion, bool IsActive, bool HasPushAddress, DateTimeOffset RegisteredAt, DateTimeOffset? LastSeenAt);
public sealed record PersonalVerification(string Method, string Status, DateTimeOffset? VerifiedAt);
public sealed record PersonalGatePass(string Status, DateTimeOffset IssuedAt, DateTimeOffset? ExpiresAt);
public sealed record PersonalVisit(
    string AppointmentNo, string Terminal, DateTimeOffset SlotStart, string Status,
    DateTimeOffset? ArrivedAt, DateTimeOffset? GateInAt, DateTimeOffset? GateOutAt,
    double[]? LastPosition, DateTimeOffset? LastPositionAt);
public sealed record PersonalPosition(string AppointmentNo, string Kind, string? Area, double Latitude, double Longitude, DateTimeOffset At);
public sealed record PersonalMessage(string Template, string Channel, string Status, string? Recipient, DateTimeOffset CreatedAt, DateTimeOffset? SentAt);

/// <summary>Everything Yardsyncs holds about one driver (PDPL right of access), as of <see cref="ExportedAt"/>.</summary>
public sealed record PersonalDataExport(
    DateTimeOffset ExportedAt, PersonalDriver Driver, IReadOnlyList<PersonalDevice> Phones, IReadOnlyList<PersonalVerification> Verifications,
    IReadOnlyList<PersonalGatePass> GatePasses, IReadOnlyList<PersonalVisit> Visits, IReadOnlyList<PersonalPosition> Positions,
    IReadOnlyList<PersonalMessage> Messages, int EventsToTerminalSystems, string Retention);

public sealed record ErasureResult(int DriverId, DateTimeOffset ErasedAt, IReadOnlyDictionary<string, int> Cleared);

/// <summary>
/// The rights of a data subject under the Saudi PDPL for drivers, the people Yardsyncs holds most about:
/// a copy of their data, and erasure. Erasure keeps the visits (the terminal's and the company's records) but
/// removes who the driver was: name, ID number, mobile, phones, positions and where messages went.
/// </summary>
public sealed class PersonalDataService(YardsyncsDbContext db, Microsoft.Extensions.Options.IOptions<PrivacyOptions> privacy, TimeProvider clock)
{
    private static readonly AppointmentStatus[] Active =
        [AppointmentStatus.Draft, AppointmentStatus.Confirmed, AppointmentStatus.EnRoute, AppointmentStatus.Arrived, AppointmentStatus.GateIn];

    public async Task<AdminResult<PersonalDataExport>> ExportAsync(CurrentUser actor, int companyId, int driverId, CancellationToken ct = default)
    {
        if (!AccessRules.CanManageFleet(actor, companyId))
            return AdminResult<PersonalDataExport>.Forbidden("Only the company's admin or a terminal admin can see a driver's personal data.");
        var driver = await db.Drivers.AsNoTracking()
            .Where(d => d.DriverId == driverId && d.CompanyId == companyId)
            .Select(d => new PersonalDriver(d.DriverId, d.CompanyId, db.Companies.Where(c => c.CompanyId == d.CompanyId).Select(c => c.Name).First(),
                d.FullName, d.IdNumber, d.Mobile, d.PreferredLanguage.Trim(), d.IsBlocked))
            .SingleOrDefaultAsync(ct);
        if (driver is null)
            return AdminResult<PersonalDataExport>.NotFound($"Driver {driverId} not found.");

        var phones = await db.DriverDevices.AsNoTracking().Where(d => d.DriverId == driverId).OrderBy(d => d.DeviceId)
            .Select(d => new PersonalDevice(d.Platform, d.AppVersion, d.IsActive, d.PushToken != null, d.RegisteredAt, d.LastSeenAt))
            .ToListAsync(ct);
        var verifications = await db.DriverVerifications.AsNoTracking().Where(v => v.DriverId == driverId).OrderBy(v => v.VerificationId)
            .Select(v => new PersonalVerification(v.Method, v.Status, v.VerifiedAt))
            .ToListAsync(ct);
        var passes = await db.GatePasses.AsNoTracking().Where(p => p.DriverId == driverId).OrderBy(p => p.GatePassId)
            .Select(p => new PersonalGatePass(p.Status, p.IssuedAt, p.ExpiresAt))
            .ToListAsync(ct);

        var visits = (await db.Appointments.AsNoTracking().Where(a => a.DriverId == driverId).OrderBy(a => a.AppointmentId)
                .Select(a => new
                {
                    a.AppointmentNo,
                    Terminal = db.Terminals.Where(t => t.TerminalId == a.TerminalId).Select(t => t.Code).First(),
                    SlotStart = a.Slot.StartAt,
                    a.Status, a.ArrivedAt, a.GateInAt, a.GateOutAt, a.LastLatitude, a.LastLongitude, a.LastPositionAt,
                })
                .ToListAsync(ct))
            .Select(a => new PersonalVisit(a.AppointmentNo, a.Terminal, a.SlotStart, a.Status.ToString(), a.ArrivedAt, a.GateInAt, a.GateOutAt,
                a.LastLatitude is { } lat && a.LastLongitude is { } lon ? [(double)lon, (double)lat] : null, a.LastPositionAt))
            .ToList();

        var positions = await db.Database.SqlQuery<PositionRow>($"""
            SELECT a.appointment_no AS "AppointmentNo", 'AREA_' || e.event_type AS "Kind", g.name AS "Area",
                   e.latitude::float8 AS "Latitude", e.longitude::float8 AS "Longitude", e.occurred_at AS "At"
            FROM geofence_event e JOIN appointment a ON a.appointment_id = e.appointment_id JOIN geofence g ON g.geofence_id = e.geofence_id
            WHERE e.driver_id = {driverId} AND e.latitude IS NOT NULL
            UNION ALL
            SELECT a.appointment_no, 'ON_THE_WAY', NULL, t.latitude::float8, t.longitude::float8, t.recorded_at
            FROM appointment_eta t JOIN appointment a ON a.appointment_id = t.appointment_id
            WHERE a.driver_id = {driverId} AND t.latitude IS NOT NULL
            ORDER BY 6
            """).ToListAsync(ct);

        var messages = await db.NotificationLogs.AsNoTracking().Where(n => n.DriverId == driverId).OrderBy(n => n.NotificationId)
            .Select(n => new { n.TemplateCode, n.Channel, n.Status, n.Recipient, n.CreatedAt, n.SentAt })
            .ToListAsync(ct);
        var events = await db.IntegrationEvents.AsNoTracking()
            .CountAsync(e => e.AppointmentId != null && db.Appointments.Any(a => a.AppointmentId == e.AppointmentId && a.DriverId == driverId), ct);

        var p = privacy.Value;
        return AdminResult<PersonalDataExport>.Ok(new PersonalDataExport(
            clock.GetUtcNow(), driver, phones, verifications, passes, visits,
            positions.Select(r => new PersonalPosition(r.AppointmentNo, r.Kind, r.Area, r.Latitude, r.Longitude, r.At)).ToList(),
            messages.Select(m => new PersonalMessage(m.TemplateCode, m.Channel.ToString(), m.Status.ToString(), m.Recipient, m.CreatedAt, m.SentAt)).ToList(),
            events,
            $"Positions are kept {p.LocationDays} days, message addresses {p.MessageDays} days, events to the terminal's systems {p.EventDays} days."));
    }

    /// <summary>Removes the driver's personal data for good. Refused while the driver has a visit that isn't over.</summary>
    public async Task<AdminResult<ErasureResult>> EraseAsync(CurrentUser actor, int companyId, int driverId, string? reason, CancellationToken ct = default)
    {
        if (!AccessRules.CanManageFleet(actor, companyId))
            return AdminResult<ErasureResult>.Forbidden("Only the company's admin or a terminal admin can erase a driver's personal data.");
        if (string.IsNullOrWhiteSpace(reason))
            return AdminResult<ErasureResult>.Invalid("Say why the data is erased (for example: the driver asked, or left the company).");
        var driver = await db.Drivers.SingleOrDefaultAsync(d => d.DriverId == driverId && d.CompanyId == companyId, ct);
        if (driver is null)
            return AdminResult<ErasureResult>.NotFound($"Driver {driverId} not found.");
        if (driver.IdNumber == Erased(driverId))
            return AdminResult<ErasureResult>.Conflict("This driver's personal data was already erased.");

        var open = await db.Appointments.AsNoTracking().Where(a => a.DriverId == driverId && Active.Contains(a.Status))
            .Select(a => a.AppointmentNo).OrderBy(n => n).Take(5).ToListAsync(ct);
        if (open.Count > 0)
            return AdminResult<ErasureResult>.Conflict(
                $"The driver still has visits that aren't over ({string.Join(", ", open)}). Assign another driver or cancel them first.");

        var now = clock.GetUtcNow();
        var mobile = driver.Mobile;
        var cleared = new Dictionary<string, int>();
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        driver.FullName = "Erased driver";
        driver.IdNumber = Erased(driverId);
        driver.Mobile = "";
        driver.IsBlocked = true;
        await db.SaveChangesAsync(ct);

        cleared["phones"] = await db.Database.ExecuteSqlAsync($"""
            UPDATE driver_device SET is_active = false, push_token = NULL, app_version = NULL, install_id = 'erased-' || device_id
            WHERE driver_id = {driverId}
            """, ct);
        cleared["gatePasses"] = await db.Database.ExecuteSqlAsync($"""
            UPDATE gate_pass SET status = 'REVOKED', revoked_reason = 'Personal data erased', pass_secret = '\x'::bytea
            WHERE driver_id = {driverId}
            """, ct);
        await db.Database.ExecuteSqlAsync($"UPDATE driver_verification SET provider_ref = NULL WHERE driver_id = {driverId}", ct);
        cleared["positions"] =
            await db.Database.ExecuteSqlAsync($"""
                UPDATE appointment SET last_latitude = NULL, last_longitude = NULL WHERE driver_id = {driverId} AND last_latitude IS NOT NULL
                """, ct)
            + await db.Database.ExecuteSqlAsync($"""
                UPDATE appointment_eta SET latitude = NULL, longitude = NULL
                WHERE latitude IS NOT NULL AND appointment_id IN (SELECT appointment_id FROM appointment WHERE driver_id = {driverId})
                """, ct)
            + await db.Database.ExecuteSqlAsync($"""
                UPDATE geofence_event SET latitude = NULL, longitude = NULL WHERE driver_id = {driverId} AND latitude IS NOT NULL
                """, ct);
        cleared["messages"] =
            await db.Database.ExecuteSqlAsync($"""
                UPDATE notification_log SET recipient = NULL, params = NULL, status = CASE WHEN status = 'QUEUED' THEN 'FAILED' ELSE status END,
                       last_error = CASE WHEN status = 'QUEUED' THEN 'Personal data erased' ELSE last_error END
                WHERE driver_id = {driverId}
                """, ct)
            + await db.Database.ExecuteSqlAsync($"""
                UPDATE appointment_message SET body = NULL, media_url = NULL WHERE driver_id = {driverId}
                """, ct);
        cleared["eventsToTerminalSystems"] = await db.Database.ExecuteSqlAsync($"""
            DELETE FROM integration_event
            WHERE status <> 'QUEUED' AND appointment_id IN (SELECT appointment_id FROM appointment WHERE driver_id = {driverId})
            """, ct);
        // The audit trail keeps that the driver was added and changed, not the values.
        cleared["auditValues"] = await db.Database.ExecuteSqlAsync($"""
            UPDATE audit_log SET old_values = NULL, new_values = NULL
            WHERE entity_name = 'driver' AND entity_key = {driverId.ToString(System.Globalization.CultureInfo.InvariantCulture)}
            """, ct);
        if (mobile.Length > 0 && !await db.Drivers.AnyAsync(d => d.Mobile == mobile, ct))
            await db.Database.ExecuteSqlAsync($"DELETE FROM driver_otp WHERE mobile = {mobile}", ct);

        AuditWriter.Add(db, null, "driver", driverId, AuditAction.Update, null, null, "Personal data erased: " + reason.Trim(), actor.UserId, now);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return AdminResult<ErasureResult>.Ok(new ErasureResult(driverId, now, cleared));
    }

    /// <summary>The ID number an erased driver keeps (unique per company, and marks the driver as erased).</summary>
    public static string Erased(int driverId) => $"ERASED-{driverId}";

    private sealed class PositionRow
    {
        public string AppointmentNo { get; set; } = "";
        public string Kind { get; set; } = "";
        public string? Area { get; set; }
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public DateTimeOffset At { get; set; }
    }
}
