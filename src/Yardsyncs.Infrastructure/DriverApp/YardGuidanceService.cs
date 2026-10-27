using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yardsyncs.Application.Auth;
using Yardsyncs.Application.Tos;
using Yardsyncs.Application.Yard;
using Yardsyncs.Domain;
using Yardsyncs.Domain.Booking;
using Yardsyncs.Domain.Parties;
using Yardsyncs.Infrastructure.Persistence;

namespace Yardsyncs.Infrastructure.DriverApp;

/// <param name="Kind">DROP (deliver a container), PICKUP (collect one) or EXIT.</param>
/// <param name="Block">Yard block; null when the terminal system has no position (follow the terminal's directions).</param>
/// <param name="Slot">Row.bay.tier inside the block as the TOS writes it.</param>
/// <param name="Moved">The position changed since the driver last looked (rehandle / housekeeping).</param>
/// <param name="Point">Where the route ends for this stop, [longitude, latitude].</param>
/// <param name="Arrived">The truck reached the block (seen by the phone's position).</param>
/// <param name="Done">The driver confirmed the stop.</param>
public sealed record GuidanceStop(
    int Sequence, string Kind, string? Block, string? Slot, IReadOnlyList<string> Containers,
    IReadOnlyList<string> CategoriesEn, IReadOnlyList<string> CategoriesAr, bool Moved, double[]? Point,
    bool Arrived = false, bool Done = false);

/// <summary>The drive to a stop: points along the terminal roads, [longitude, latitude], and the turns at junctions on the way.</summary>
public sealed record GuidanceLeg(int ToStop, IReadOnlyList<double[]> Path, double Meters, IReadOnlyList<TurnStep>? Turns = null);

/// <param name="Kind">Area type: YARD_ZONE, CFS, GATE, EXIT…</param>
public sealed record MapArea(string Name, string Kind, string? Block, IReadOnlyList<double[]> Ring);

/// <param name="Bounds">[minLon, minLat, maxLon, maxLat] of everything on the map.</param>
public sealed record YardMapView(IReadOnlyList<MapArea> Areas, IReadOnlyList<IReadOnlyList<double[]>> Roads, double[] Bounds);

/// <summary>The in-gate ticket shown in the app: when and where the truck passed the gate.</summary>
public sealed record GateTicket(string TerminalName, DateTimeOffset? GateInAt, string? Lane, string? TruckPlate);

/// <param name="Live">False when the terminal system could not be reached and the last known positions are shown.</param>
public sealed record YardGuidance(
    string AppointmentNo, AppointmentStatus Status, IReadOnlyList<GuidanceStop> Stops, IReadOnlyList<GuidanceLeg> Legs, YardMapView Map, bool Live,
    GateTicket? Ticket = null);

public sealed record RoadsInput(JsonElement Roads);

/// <summary>
/// Directions inside the terminal after gate-in: the TOS yard position of each move, in driving order (deliveries
/// first, then pickups, then the exit), with routes along the terminal's own roads.
/// </summary>
public sealed class YardGuidanceService(YardsyncsDbContext db, ITosAdapter tos, TripTrackingService areas, TimeProvider clock)
{
    /// <summary>
    /// The driver pressed "Done here": the stop is DONE, the next one becomes ACTIVE, and STOP_COMPLETED goes to the
    /// terminal's systems. Returns an error (null = done) and whether the appointment / stop wasn't found.
    /// </summary>
    public async Task<(string? Error, bool NotFound)> CompleteStopAsync(CurrentDriver driver, string appointmentNo, int sequence, CancellationToken ct = default)
    {
        var number = appointmentNo.Trim().ToUpperInvariant();
        var appointment = await db.Appointments
            .Where(a => a.AppointmentNo == number && a.DriverId != null
                        && db.Drivers.Any(d => d.DriverId == a.DriverId && d.Mobile == driver.Mobile && !d.IsBlocked))
            .SingleOrDefaultAsync(ct);
        if (appointment is null)
            return ($"Appointment {number} not found.", true);
        if (appointment.Status != AppointmentStatus.GateIn)
            return ("Stops can be confirmed inside the terminal only.", false);

        var stops = await db.YardInstructions.Where(y => y.AppointmentId == appointment.AppointmentId).OrderBy(y => y.Sequence).ToListAsync(ct);
        var stop = stops.FirstOrDefault(y => y.Sequence == sequence);
        if (stop is null || stop.StopType == "EXIT")
            return ($"Stop {sequence} not found.", true);
        if (stop.Status == "DONE")
            return (null, false); // pressed twice

        var now = clock.GetUtcNow();
        stop.Status = "DONE";
        stop.ArrivedAt ??= now;
        stop.CompletedAt = now;
        if (stops.FirstOrDefault(y => y.Sequence > sequence && y.Status == "PENDING") is { } next)
            next.Status = "ACTIVE";
        Yardsyncs.Infrastructure.Integration.IntegrationEventQueue.Add(db, appointment.TerminalId, IntegrationEventTypes.StopCompleted, appointment.AppointmentId,
            await areas.StopEventAsync(appointment, stop, null, null, now, ct), now);
        await db.SaveChangesAsync(ct);
        return (null, false);
    }

    public async Task<(YardGuidance? Guidance, string? Error, bool NotFound)> ForDriverAsync(
        CurrentDriver driver, string appointmentNo, double? latitude, double? longitude, CancellationToken ct = default)
    {
        var number = appointmentNo.Trim().ToUpperInvariant();
        var appointment = await db.Appointments
            .Include(a => a.Slot)
            .Include(a => a.Items).ThenInclude(i => i.Category)
            .Where(a => a.AppointmentNo == number && a.DriverId != null
                        && db.Drivers.Any(d => d.DriverId == a.DriverId && d.Mobile == driver.Mobile && !d.IsBlocked))
            .SingleOrDefaultAsync(ct);
        if (appointment is null)
            return (null, $"Appointment {number} not found.", true);

        // Positions are shown inside the terminal only (the container's place is not given to trucks outside).
        if (appointment.Status != AppointmentStatus.GateIn)
            return (null, "Directions in the yard appear after you pass the gate.", false);

        var start = latitude is { } lat && longitude is { } lon ? new GeoPoint(lon, lat) : (GeoPoint?)null;
        var guidance = await BuildAsync(appointment, start, ct);
        return (guidance with { Ticket = await TicketAsync(appointment, ct) }, null, false);
    }

    /// <summary>
    /// At gate-in: reads the yard positions from the TOS and issues the stops (yard_instruction), so the in-gate
    /// ticket and the push to the driver carry the block to drive to. Null when the appointment isn't inside.
    /// </summary>
    public async Task<YardGuidance?> IssueAtGateInAsync(long appointmentId, CancellationToken ct = default)
    {
        var appointment = await db.Appointments
            .Include(a => a.Slot)
            .Include(a => a.Items).ThenInclude(i => i.Category)
            .SingleOrDefaultAsync(a => a.AppointmentId == appointmentId && a.Status == AppointmentStatus.GateIn, ct);
        return appointment is null ? null : await BuildAsync(appointment, null, ct);
    }

    /// <summary>"B2 (14.03.2) → E1": the blocks in driving order, the same in every language.</summary>
    public static string StopsText(IEnumerable<GuidanceStop> stops) =>
        string.Join(" → ", stops.Where(s => s.Kind != "EXIT" && s.Block is not null)
            .Select(s => s.Slot is null ? s.Block! : $"{s.Block} ({s.Slot})"));

    private async Task<GateTicket> TicketAsync(Appointment appointment, CancellationToken ct)
    {
        var terminal = await db.Terminals.AsNoTracking().Where(t => t.TerminalId == appointment.TerminalId).Select(t => t.Name).SingleAsync(ct);
        var lane = await db.GateEvents.AsNoTracking()
            .Where(e => e.AppointmentId == appointment.AppointmentId && e.EventType == Yardsyncs.Domain.Gate.GateEventType.GateIn && e.LaneId != null)
            .OrderByDescending(e => e.OccurredAt)
            .Select(e => db.GateLanes.Where(l => l.LaneId == e.LaneId).Select(l => l.LaneCode).FirstOrDefault())
            .FirstOrDefaultAsync(ct);
        var plate = appointment.TruckId is { } truckId
            ? await db.Trucks.AsNoTracking().Where(t => t.TruckId == truckId).Select(t => t.PlateNumber).SingleOrDefaultAsync(ct)
            : null;
        return new GateTicket(terminal, appointment.GateInAt, lane, plate);
    }

    private async Task<YardGuidance> BuildAsync(Appointment appointment, GeoPoint? position, CancellationToken ct)
    {
        var map = await MapAsync(appointment.TerminalId, ct);
        var graph = new RoadGraph(map.Roads.Select(r => new Road(r.Points, r.OneWay)));
        // Junctions are named by the block (or parking, CFS, gate…) beside them; the approach road outside is not a landmark.
        var landmarks = map.Areas.Where(x => x.Kind != "APPROACH" && x.Ring.Count > 0)
            .Select(x => new Landmark(x.Block ?? x.Name, x.Block is not null, RoadGraph.Centroid(Points(x.Ring))))
            .ToList();

        // Moves in driving order: deliveries first, then pickups.
        var items = appointment.Items
            .Select((item, index) => (Item: item, Index: index, Pickup: IsPickup(item.Category)))
            .OrderBy(x => x.Pickup)
            .ThenBy(x => x.Index)
            .ToList();

        var live = true;
        IReadOnlyList<TosYardPosition> positions;
        try
        {
            positions = await tos.YardPositionsAsync(appointment.TerminalId,
                items.Select(x => new TosItemQuery(x.Index, x.Item.Category.Code, x.Item.Category.Mode, x.Item.ContainerNo,
                    x.Item.BookingRef, x.Item.EdoRef, null, appointment.Slot.StartAt, x.Item.Vin)).ToList(), ct);
        }
        catch (TosUnavailableException)
        {
            live = false;
            positions = Array.Empty<TosYardPosition>();
        }

        var stored = await db.YardInstructions.Where(y => y.AppointmentId == appointment.AppointmentId).ToListAsync(ct);

        // One stop per block in order; moves without a known position get their own stop.
        var stops = new List<(string Kind, string? Block, string? Slot, List<AppointmentItem> Items)>();
        foreach (var x in items)
        {
            var pos = positions.FirstOrDefault(p => p.ItemIndex == x.Index);
            var block = pos?.Block ?? pos?.TransferPoint;
            var slot = pos?.Slot;
            if (!live && stored.FirstOrDefault(s => s.ItemId == x.Item.ItemId) is { } last)
            {
                block = last.Block;
                slot = last.Bay;
            }
            var kind = x.Pickup ? "PICKUP" : "DROP";
            if (stops.Count > 0 && block is not null && stops[^1].Block == block && stops[^1].Kind == kind && slot is null && stops[^1].Slot is null)
                stops[^1].Items.Add(x.Item);
            else
                stops.Add((kind, block, slot, new List<AppointmentItem> { x.Item }));
        }

        var now = clock.GetUtcNow();
        var gate = map.Areas.FirstOrDefault(a => a.Kind == "GATE");
        var exit = map.Areas.FirstOrDefault(a => a.Kind == "EXIT") ?? gate;
        var here = position is { } p && Inside(map.Bounds, p) ? p : gate is null ? (GeoPoint?)null : RoadGraph.Centroid(Points(gate.Ring));

        var result = new List<GuidanceStop>();
        var legs = new List<GuidanceLeg>();
        var sequence = 0;
        foreach (var stop in stops)
        {
            sequence++;
            var area = stop.Block is null ? null : map.Areas.FirstOrDefault(a => string.Equals(a.Block, stop.Block, StringComparison.OrdinalIgnoreCase));
            GeoPoint? target = area is null ? null : RoadGraph.Centroid(Points(area.Ring));

            var existing = stored.FirstOrDefault(s => s.Sequence == sequence);
            var moved = existing is not null && existing.Status != "DONE" && (existing.Block != stop.Block || existing.Bay != stop.Slot);
            if (moved && existing!.ArrivedAt is not null && existing.Block != stop.Block)
            {
                // Arrived at the old block, but the container is elsewhere now: not there yet.
                existing.ArrivedAt = null;
                existing.Status = "ACTIVE";
            }
            Upsert(appointment.AppointmentId, stop.Items[0].ItemId, sequence, "YARD_BLOCK", stop.Block, stop.Slot, existing, now,
                $"Block {stop.Block ?? "?"}{(stop.Slot is null ? "" : $", slot {stop.Slot}")}",
                $"البلوك {stop.Block ?? "?"}{(stop.Slot is null ? "" : $"، الموقع {stop.Slot}")}");

            double[]? point = null;
            if (here is { } from && target is { } to)
            {
                var (path, meters) = graph.Route(from, to);
                if (path.Count > 0)
                {
                    legs.Add(new GuidanceLeg(sequence, path.Select(x => x.ToArray()).ToList(), meters,
                        TurnByTurn.Steps(path, graph.IsJunction, landmarks)));
                    here = path[^1];
                    point = path[^1].ToArray();
                }
            }
            point ??= target?.ToArray();

            result.Add(new GuidanceStop(sequence, stop.Kind, stop.Block, stop.Slot,
                stop.Items.Select(i => i.ContainerNo ?? i.Vin ?? i.BookingRef ?? i.Category.Code).ToList(),
                stop.Items.Select(i => i.Category.NameEn).Distinct().ToList(),
                stop.Items.Select(i => i.Category.NameAr).Distinct().ToList(),
                moved, point, existing?.ArrivedAt is not null, existing?.Status == "DONE"));
        }

        // Then out of the terminal.
        sequence++;
        double[]? exitPoint = exit is null ? null : RoadGraph.Centroid(Points(exit.Ring)).ToArray();
        if (here is { } last2 && exit is not null)
        {
            var (path, meters) = graph.Route(last2, RoadGraph.Centroid(Points(exit.Ring)));
            if (path.Count > 0)
            {
                legs.Add(new GuidanceLeg(sequence, path.Select(x => x.ToArray()).ToList(), meters,
                    TurnByTurn.Steps(path, graph.IsJunction, landmarks)));
                exitPoint = path[^1].ToArray();
            }
        }
        result.Add(new GuidanceStop(sequence, "EXIT", null, null, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), false, exitPoint));
        Upsert(appointment.AppointmentId, null, sequence, "EXIT", null, null, stored.FirstOrDefault(s => s.Sequence == sequence), now, "Exit", "الخروج");

        await db.SaveChangesAsync(ct);

        return new YardGuidance(appointment.AppointmentNo, appointment.Status, result, legs,
            new YardMapView(map.Areas, map.Roads.Select(r => (IReadOnlyList<double[]>)r.Points.Select(x => x.ToArray()).ToList()).ToList(), map.Bounds), live);
    }

    private void Upsert(long appointmentId, long? itemId, int sequence, string stopType, string? block, string? slot,
        YardInstruction? row, DateTimeOffset now, string textEn, string textAr)
    {
        if (row is null)
        {
            row = new YardInstruction { AppointmentId = appointmentId, Sequence = sequence, IssuedAt = now, Source = "TOS" };
            db.YardInstructions.Add(row);
        }
        row.ItemId = itemId;
        row.StopType = stopType;
        row.Block = Truncate(block, 20);
        row.Bay = Truncate(slot, 10);
        row.TextEn = Truncate(textEn, 300);
        row.TextAr = Truncate(textAr, 300);
        if (row.Status == "PENDING" && sequence == 1)
            row.Status = "ACTIVE";
    }

    /// <summary>Collect moves (imports, empties out, cargo, vehicles taken out); everything else is a delivery into the terminal.</summary>
    internal static bool IsPickup(TransactionCategory category) =>
        category.Mode == CategoryMode.Cargo
        || (category.Mode == CategoryMode.Vehicle && category.Flow != CategoryFlow.In
            && !category.Code.Contains("EXP", StringComparison.OrdinalIgnoreCase)
            && !category.Code.Contains("RECEIV", StringComparison.OrdinalIgnoreCase))
        || category.Code.StartsWith("IMP", StringComparison.OrdinalIgnoreCase)
        || category.Code.EndsWith("_PICKUP", StringComparison.OrdinalIgnoreCase);

    // ================================================================ the terminal map

    private sealed record MapData(IReadOnlyList<MapArea> Areas, IReadOnlyList<(IReadOnlyList<GeoPoint> Points, bool OneWay)> Roads, double[] Bounds);

    private static readonly string[] MapKinds = ["YARD_ZONE", "CFS", "GATE", "EXIT", "PARKING"];

    /// <summary>Blocks, gates and exits (terminal areas) and the roads, for the app's map.</summary>
    public async Task<YardMapView> MapViewAsync(int terminalId, CancellationToken ct = default)
    {
        var map = await MapAsync(terminalId, ct);
        return new YardMapView(map.Areas, map.Roads.Select(r => (IReadOnlyList<double[]>)r.Points.Select(x => x.ToArray()).ToList()).ToList(), map.Bounds);
    }

    private async Task<MapData> MapAsync(int terminalId, CancellationToken ct)
    {
        var areaRows = (await areas.ListAsync(terminalId, ct)).Where(a => a.IsActive && MapKinds.Contains(a.ZoneType)).ToList();
        var mapAreas = areaRows
            .Select(a => new MapArea(a.Name, a.ZoneType, a.YardBlock, Ring(a.GeoJson)))
            .Where(a => a.Ring.Count >= 4)
            .ToList();

        var roads = new List<(IReadOnlyList<GeoPoint>, bool)>();
        var json = await db.TerminalMaps.AsNoTracking().Where(m => m.TerminalId == terminalId).Select(m => m.Roads).SingleOrDefaultAsync(ct);
        if (json is not null)
            roads.AddRange(ParseRoads(JsonDocument.Parse(json).RootElement, out _));

        var all = mapAreas.SelectMany(a => a.Ring).Select(p => new GeoPoint(p[0], p[1]))
            .Concat(roads.SelectMany(r => r.Item1)).ToList();
        var bounds = all.Count == 0
            ? new double[] { 0, 0, 0, 0 }
            : new[] { all.Min(p => p.Lon), all.Min(p => p.Lat), all.Max(p => p.Lon), all.Max(p => p.Lat) };
        return new MapData(mapAreas, roads, bounds);
    }

    /// <summary>The roads as saved (GeoJSON FeatureCollection, with names and one-way flags), for the map editor.</summary>
    public async Task<string> RoadsAsync(int terminalId, CancellationToken ct = default) =>
        await db.TerminalMaps.AsNoTracking().Where(m => m.TerminalId == terminalId).Select(m => m.Roads).SingleOrDefaultAsync(ct)
        ?? """{"type":"FeatureCollection","features":[]}""";

    /// <summary>Saves the roads (a GeoJSON FeatureCollection of LineStrings, property oneWay).</summary>
    public async Task<IReadOnlyList<string>> SaveRoadsAsync(int terminalId, int userId, JsonElement roads, CancellationToken ct = default)
    {
        var parsed = ParseRoads(roads, out var errors);
        if (errors.Count == 0 && parsed.Count == 0)
            errors.Add("Draw at least one road.");
        if (errors.Count > 0)
            return errors;

        var map = await db.TerminalMaps.SingleOrDefaultAsync(m => m.TerminalId == terminalId, ct);
        if (map is null)
        {
            map = new TerminalMap { TerminalId = terminalId };
            db.TerminalMaps.Add(map);
        }
        map.Roads = roads.GetRawText();
        map.UpdatedAt = clock.GetUtcNow();
        map.UpdatedBy = userId;
        await db.SaveChangesAsync(ct);
        return Array.Empty<string>();
    }

    public static List<(IReadOnlyList<GeoPoint> Points, bool OneWay)> ParseRoads(JsonElement root, out List<string> errors)
    {
        errors = new List<string>();
        var roads = new List<(IReadOnlyList<GeoPoint>, bool)>();
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("features", out var features) || features.ValueKind != JsonValueKind.Array)
        {
            errors.Add("Roads must be a GeoJSON FeatureCollection.");
            return roads;
        }

        var n = 0;
        foreach (var feature in features.EnumerateArray())
        {
            n++;
            if (!feature.TryGetProperty("geometry", out var geometry) || geometry.ValueKind != JsonValueKind.Object
                || !geometry.TryGetProperty("type", out var type) || type.GetString() != "LineString"
                || !geometry.TryGetProperty("coordinates", out var coordinates) || coordinates.ValueKind != JsonValueKind.Array)
            {
                errors.Add($"Road {n}: must be a LineString.");
                continue;
            }

            var points = new List<GeoPoint>();
            foreach (var c in coordinates.EnumerateArray())
            {
                if (c.ValueKind != JsonValueKind.Array || c.GetArrayLength() < 2)
                    continue;
                var lon = c[0].GetDouble();
                var lat = c[1].GetDouble();
                if (lon is < -180 or > 180 || lat is < -90 or > 90)
                {
                    errors.Add($"Road {n}: a point is outside longitude / latitude range.");
                    break;
                }
                points.Add(new GeoPoint(lon, lat));
            }
            if (points.Count < 2)
            {
                errors.Add($"Road {n}: needs at least 2 points.");
                continue;
            }

            var oneWay = feature.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object
                         && props.TryGetProperty("oneWay", out var ow) && ow.ValueKind == JsonValueKind.True;
            roads.Add((points, oneWay));
        }
        return roads;
    }

    private static IReadOnlyList<double[]> Ring(string geoJson)
    {
        using var doc = JsonDocument.Parse(geoJson);
        if (!doc.RootElement.TryGetProperty("coordinates", out var rings) || rings.GetArrayLength() == 0)
            return Array.Empty<double[]>();
        return rings[0].EnumerateArray().Select(c => new[] { c[0].GetDouble(), c[1].GetDouble() }).ToList();
    }

    private static IReadOnlyList<GeoPoint> Points(IReadOnlyList<double[]> ring) => ring.Select(p => new GeoPoint(p[0], p[1])).ToList();

    /// <summary>A position counts only if it is on (or right next to) the terminal map.</summary>
    private static bool Inside(double[] b, GeoPoint p) =>
        p.Lon >= b[0] - 0.002 && p.Lon <= b[2] + 0.002 && p.Lat >= b[1] - 0.002 && p.Lat <= b[3] + 0.002;

    private static string? Truncate(string? value, int max) => value is null || value.Length <= max ? value : value[..max];
}
