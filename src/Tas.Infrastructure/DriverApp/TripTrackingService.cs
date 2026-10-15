using Microsoft.EntityFrameworkCore;
using Tas.Application.Auth;
using Tas.Domain.Booking;
using Tas.Domain.Gate;
using Tas.Domain.Parties;
using Tas.Infrastructure.Persistence;

namespace Tas.Infrastructure.DriverApp;

/// <param name="Eta">Estimated arrival at the terminal gate; null once checked in or when the terminal has no gate area.</param>
/// <param name="CheckedIn">The truck just entered a check-in area (pre-gate) and is now at the gate.</param>
public sealed record TripUpdate(AppointmentStatus Status, DateTimeOffset? Eta, double? DistanceKm, bool CheckedIn, IReadOnlyList<string> Areas);

/// <summary>A terminal area; <see cref="GeoJson"/> is the polygon as GeoJSON.</summary>
public sealed class GeofenceView
{
    public int GeofenceId { get; set; }
    public string Name { get; set; } = "";
    public string ZoneType { get; set; } = "";
    public string? YardBlock { get; set; }
    public bool TriggersCheckIn { get; set; }
    public bool IsActive { get; set; }
    public string GeoJson { get; set; } = "";
}

/// <summary>An area a position falls in (raw SQL result).</summary>
public sealed class GeofenceHit
{
    public int GeofenceId { get; set; }
    public string ZoneType { get; set; } = "";
    public bool TriggersCheckIn { get; set; }
}

/// <summary>A terminal area as drawn by the terminal admin: a closed ring of [longitude, latitude] points.</summary>
public sealed record GeofenceInput(string Name, string ZoneType, IReadOnlyList<double[]> Ring, string? YardBlock = null, bool TriggersCheckIn = false);

/// <summary>
/// Positions from the driver app while the truck is on the way: arrival estimate, entering terminal areas and
/// automatic check-in at the pre-gate. Positions are only accepted while the appointment is EN_ROUTE (privacy).
/// </summary>
public sealed class TripTrackingService(TasDbContext db, TimeProvider clock)
{
    public static readonly string[] ZoneTypes = ["APPROACH", "PARKING", "PRE_GATE", "GATE", "YARD_ZONE", "CFS", "EXIT"];

    /// <summary>Assumed speed when the phone doesn't report one (≈ 40 km/h), and the road / straight-line factor.</summary>
    public const double DefaultSpeedMps = 11;
    public const double RoadFactor = 1.3;

    public async Task<(TripUpdate? Update, string? Error, bool NotFound)> ReportAsync(
        CurrentDriver driver, string appointmentNo, double latitude, double longitude, double? speedMps, CancellationToken ct = default)
    {
        if (latitude is < -90 or > 90 || longitude is < -180 or > 180)
            return (null, "Position out of range.", false);

        var number = appointmentNo.Trim().ToUpperInvariant();
        var appointment = await db.Appointments
            .Where(a => a.AppointmentNo == number && a.DriverId != null
                        && db.Drivers.Any(d => d.DriverId == a.DriverId && d.Mobile == driver.Mobile && !d.IsBlocked))
            .SingleOrDefaultAsync(ct);
        if (appointment is null)
            return (null, $"Appointment {number} not found.", true);
        if (appointment.Status != AppointmentStatus.EnRoute)
            return (null, "Your position is only shared while you are on the way to the terminal.", false);

        var now = clock.GetUtcNow();
        var hits = await db.Database.SqlQuery<GeofenceHit>($"""
            SELECT geofence_id AS "GeofenceId", zone_type AS "ZoneType", triggers_check_in AS "TriggersCheckIn"
            FROM geofence
            WHERE terminal_id = {appointment.TerminalId} AND is_active
              AND ST_Covers(area, ST_SetSRID(ST_MakePoint({longitude}, {latitude}), 4326)::geography)
            """).ToListAsync(ct);

        // Enter events, once per area per appointment.
        var seen = await db.GeofenceEvents.Where(e => e.AppointmentId == appointment.AppointmentId && e.EventType == "ENTER")
            .Select(e => e.GeofenceId).ToListAsync(ct);
        foreach (var hit in hits.Where(h => !seen.Contains(h.GeofenceId)))
        {
            db.GeofenceEvents.Add(new GeofenceEvent
            {
                AppointmentId = appointment.AppointmentId,
                DriverId = appointment.DriverId!.Value,
                GeofenceId = hit.GeofenceId,
                EventType = "ENTER",
                Latitude = Math.Round((decimal)latitude, 6),
                Longitude = Math.Round((decimal)longitude, 6),
                OccurredAt = now,
            });
        }

        var checkedIn = false;
        if (hits.Any(h => h.TriggersCheckIn))
        {
            appointment.Status = AppointmentStatus.Arrived;
            appointment.ArrivedAt = now;
            appointment.CheckInMethod = "APP_GEOFENCE";
            appointment.CurrentEta = null;
            db.AppointmentStatusHistory.Add(new AppointmentStatusHistory
            {
                AppointmentId = appointment.AppointmentId,
                FromStatus = AppointmentStatus.EnRoute,
                ToStatus = AppointmentStatus.Arrived,
                Reason = "Checked in by the driver app at the pre-gate",
                ChangedAt = now,
            });
            db.GateEvents.Add(new GateEvent
            {
                TerminalId = appointment.TerminalId,
                AppointmentId = appointment.AppointmentId,
                EventType = GateEventType.AppCheckin,
                Details = "Entered the pre-gate area",
                OccurredAt = now,
            });
            checkedIn = true;
        }

        DateTimeOffset? eta = null;
        double? distanceKm = null;
        if (!checkedIn)
        {
            var meters = await db.Database.SqlQuery<double>($"""
                SELECT ST_Distance(ST_Centroid(area::geometry)::geography, ST_SetSRID(ST_MakePoint({longitude}, {latitude}), 4326)::geography) AS "Value"
                FROM geofence
                WHERE terminal_id = {appointment.TerminalId} AND is_active AND zone_type IN ('PRE_GATE', 'GATE')
                ORDER BY 1
                LIMIT 1
                """).ToListAsync(ct);
            if (meters.Count > 0)
            {
                var speed = speedMps is > 3 and < 40 ? speedMps.Value : DefaultSpeedMps;
                distanceKm = Math.Round(meters[0] / 1000, 2);
                eta = now.AddSeconds(meters[0] * RoadFactor / speed);
                appointment.CurrentEta = eta;
                appointment.EtaUpdatedAt = now;
                db.AppointmentEtas.Add(new AppointmentEta
                {
                    AppointmentId = appointment.AppointmentId,
                    EtaAt = eta.Value,
                    DistanceKm = (decimal)distanceKm.Value,
                    Latitude = Math.Round((decimal)latitude, 6),
                    Longitude = Math.Round((decimal)longitude, 6),
                    Source = "APP_GPS",
                    RecordedAt = now,
                });
            }
        }

        await db.SaveChangesAsync(ct);
        return (new TripUpdate(appointment.Status, eta, distanceKm, checkedIn, hits.Select(h => h.ZoneType).Distinct().ToList()), null, false);
    }

    // ================================================================ terminal areas

    public Task<List<GeofenceView>> ListAsync(int terminalId, CancellationToken ct = default) =>
        db.Database.SqlQuery<GeofenceView>($"""
            SELECT geofence_id AS "GeofenceId", name AS "Name", zone_type AS "ZoneType", yard_block AS "YardBlock",
                   triggers_check_in AS "TriggersCheckIn", is_active AS "IsActive", ST_AsGeoJSON(area) AS "GeoJson"
            FROM geofence WHERE terminal_id = {terminalId} ORDER BY geofence_id
            """).ToListAsync(ct);

    /// <summary>Adds an area; returns its id, or the problems with the input.</summary>
    public async Task<(int? Id, IReadOnlyList<string> Errors)> CreateAsync(int terminalId, GeofenceInput input, CancellationToken ct = default)
    {
        var (zone, block, wkt, errors) = await ValidateAsync(terminalId, null, input, ct);
        if (errors.Count > 0)
            return (null, errors);

        var ids = await db.Database.SqlQuery<int>($"""
            INSERT INTO geofence (terminal_id, name, zone_type, area, yard_block, triggers_check_in)
            VALUES ({terminalId}, {input.Name.Trim()}, {zone}, ST_GeogFromText({wkt}), {block}, {input.TriggersCheckIn})
            RETURNING geofence_id AS "Value"
            """).ToListAsync(ct);
        return (ids[0], Array.Empty<string>());
    }

    /// <summary>Changes an active area's name, kind, shape or block. NotFound when there is no such active area.</summary>
    public async Task<(bool NotFound, IReadOnlyList<string> Errors)> UpdateAsync(int terminalId, int geofenceId, GeofenceInput input, CancellationToken ct = default)
    {
        var exists = await db.Database.SqlQuery<int>($"""
            SELECT geofence_id AS "Value" FROM geofence WHERE terminal_id = {terminalId} AND geofence_id = {geofenceId} AND is_active
            """).AnyAsync(ct);
        if (!exists)
            return (true, Array.Empty<string>());

        var (zone, block, wkt, errors) = await ValidateAsync(terminalId, geofenceId, input, ct);
        if (errors.Count > 0)
            return (false, errors);

        await db.Database.ExecuteSqlAsync($"""
            UPDATE geofence SET name = {input.Name.Trim()}, zone_type = {zone}, area = ST_GeogFromText({wkt}),
                   yard_block = {block}, triggers_check_in = {input.TriggersCheckIn}
            WHERE terminal_id = {terminalId} AND geofence_id = {geofenceId}
            """, ct);
        return (false, Array.Empty<string>());
    }

    private async Task<(string Zone, string? Block, string Wkt, List<string> Errors)> ValidateAsync(
        int terminalId, int? geofenceId, GeofenceInput input, CancellationToken ct)
    {
        var errors = new List<string>();
        var zone = input.ZoneType?.Trim().ToUpperInvariant() ?? "";
        if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Trim().Length > 100)
            errors.Add("Give the area a name (up to 100 characters).");
        if (!ZoneTypes.Contains(zone))
            errors.Add($"Zone type must be one of {string.Join(", ", ZoneTypes)}.");

        var block = string.IsNullOrWhiteSpace(input.YardBlock) ? null : input.YardBlock.Trim().ToUpperInvariant();
        if (zone == "YARD_ZONE" && block is null)
            errors.Add("A yard block needs the block name the TOS uses (for example A01).");
        else if (block is { Length: > 20 })
            errors.Add("The block name can be up to 20 characters.");
        else if (block is not null && zone == "YARD_ZONE")
        {
            var others = await db.Database.SqlQuery<int>($"""
                SELECT geofence_id AS "Value" FROM geofence
                WHERE terminal_id = {terminalId} AND is_active AND zone_type = 'YARD_ZONE' AND upper(yard_block) = {block}
                """).ToListAsync(ct);
            if (others.Any(id => id != geofenceId))
                errors.Add($"Block {block} is already drawn on the map.");
        }

        var ring = (input.Ring ?? Array.Empty<double[]>()).ToList();
        if (ring.Count < 4 || ring.Any(p => p is not { Length: 2 } || p[0] is < -180 or > 180 || p[1] is < -90 or > 90))
            errors.Add("The area needs at least 4 [longitude, latitude] points.");
        else if (ring[0][0] != ring[^1][0] || ring[0][1] != ring[^1][1])
            ring.Add(ring[0]); // close the ring

        var wkt = errors.Count > 0 ? "" : "POLYGON((" + string.Join(", ", ring.Select(p => FormattableString.Invariant($"{p[0]} {p[1]}"))) + "))";
        return (zone, zone == "YARD_ZONE" || zone == "CFS" ? block : null, wkt, errors);
    }

    public Task<int> DeactivateAsync(int terminalId, int geofenceId, CancellationToken ct = default) =>
        db.Database.ExecuteSqlAsync($"UPDATE geofence SET is_active = false WHERE terminal_id = {terminalId} AND geofence_id = {geofenceId}", ct);
}
