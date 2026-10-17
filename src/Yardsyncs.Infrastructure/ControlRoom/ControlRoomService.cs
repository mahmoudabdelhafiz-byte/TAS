using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Yardsyncs.Domain;
using Yardsyncs.Domain.Booking;
using Yardsyncs.Infrastructure.DriverApp;
using Yardsyncs.Infrastructure.Persistence;

namespace Yardsyncs.Infrastructure.ControlRoom;

/// <param name="Status">PENDING, ACTIVE, ARRIVED, DONE or CANCELLED (yard_instruction).</param>
public sealed record ControlStop(int Sequence, string StopType, string? Block, string? Slot, string Status, DateTimeOffset? ArrivedAt, DateTimeOffset? CompletedAt);

/// <param name="Stage">ON_THE_WAY, AT_GATE, TO_BLOCK, AT_BLOCK or TO_EXIT.</param>
/// <param name="Position">Last app position [longitude, latitude], on the way / inside only.</param>
/// <param name="Overdue">Inside longer than the terminal's turn-time target.</param>
/// <param name="Silent">The app sent positions inside but none for a while (phone off, app closed, no signal).</param>
public sealed record ControlTruck(
    string AppointmentNo, AppointmentStatus Status, string Stage, string Company, string? TruckPlate, string? DriverName,
    DateTimeOffset SlotStart, DateTimeOffset SlotEnd, DateTimeOffset? Eta, DateTimeOffset? ArrivedAt, DateTimeOffset? GateInAt,
    int? MinutesInside, double[]? Position, DateTimeOffset? PositionAt, IReadOnlyList<string> Moves,
    IReadOnlyList<ControlStop> Stops, int? CurrentStop, bool Overdue, bool Silent,
    int? DriverId = null, int DriverPoints = 0, Yardsyncs.Application.MasterData.DriverStanding DriverStanding = Yardsyncs.Application.MasterData.DriverStanding.Clear);

/// <param name="AvgMinutesToBlock">Gate-in to arrival at the first block, today.</param>
/// <param name="AvgMinutesAtBlock">Arrival at a block to "Done here", today.</param>
public sealed record ControlStats(
    int OnTheWay, int AtGate, int Inside, int CompletedToday, double? AvgMinutesInside, double? AvgMinutesToBlock, double? AvgMinutesAtBlock);

public sealed record ControlRoomBoard(DateTimeOffset Now, int TurnTargetMinutes, ControlStats Stats, IReadOnlyList<ControlTruck> Trucks, YardMapView Map);

/// <summary>
/// The control room: every live visit (on the way, at the gate, inside) with its stops in the yard, where the truck
/// was last seen, and today's times — gate to block, at the block, inside the terminal.
/// </summary>
public sealed class ControlRoomService(YardsyncsDbContext db, YardGuidanceService yard, TimeProvider clock)
{
    /// <summary>terminal_setting key: minutes inside the terminal after which a truck is flagged (default 60).</summary>
    public const string TurnTargetKey = "turn_target_min";

    public const int DefaultTurnTargetMinutes = 60;

    /// <summary>No position for this long while the app was sending them inside → flagged.</summary>
    public static readonly TimeSpan SilentAfter = TimeSpan.FromMinutes(5);

    public async Task<ControlRoomBoard> BoardAsync(int terminalId, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var terminal = await db.Terminals.AsNoTracking().SingleAsync(t => t.TerminalId == terminalId, ct);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(terminal.TimeZone);
        var localToday = TimeZoneInfo.ConvertTime(now, zone).Date;
        var dayStart = new DateTimeOffset(localToday, zone.GetUtcOffset(localToday)).ToUniversalTime();

        var target = await db.TerminalSettings.AsNoTracking()
            .Where(s => s.TerminalId == terminalId && s.SettingKey == TurnTargetKey)
            .Select(s => s.SettingValue)
            .FirstOrDefaultAsync(ct) is { } raw && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes) && minutes > 0
            ? minutes
            : DefaultTurnTargetMinutes;

        var live = await db.Appointments.AsNoTracking()
            .Where(a => a.TerminalId == terminalId
                        && (a.Status == AppointmentStatus.EnRoute || a.Status == AppointmentStatus.Arrived || a.Status == AppointmentStatus.GateIn))
            .Select(a => new
            {
                a.AppointmentId, a.AppointmentNo, a.Status, Company = a.Company.Name,
                Plate = a.Truck != null ? a.Truck.PlateNumber : null,
                Driver = a.Driver != null ? a.Driver.FullName : null,
                a.DriverId,
                DriverMobile = a.Driver != null ? a.Driver.Mobile : null,
                a.Slot.StartAt, a.Slot.EndAt, a.CurrentEta, a.ArrivedAt, a.GateInAt,
                a.LastLatitude, a.LastLongitude, a.LastPositionAt,
                Moves = a.Items.Select(i => i.ContainerNo ?? i.BookingRef ?? i.Category.Code).ToList(),
            })
            .ToListAsync(ct);

        var ids = live.Select(a => a.AppointmentId).ToList();
        var stopsByAppointment = (await db.YardInstructions.AsNoTracking()
                .Where(y => ids.Contains(y.AppointmentId))
                .OrderBy(y => y.Sequence)
                .ToListAsync(ct))
            .GroupBy(y => y.AppointmentId)
            .ToDictionary(g => g.Key, g => g.Select(y => new ControlStop(y.Sequence, y.StopType, y.Block, y.Bay, y.Status, y.ArrivedAt, y.CompletedAt)).ToList());

        // Drivers' penalty points at this terminal (the record), per person.
        var settings = Yardsyncs.Domain.TerminalSettings.From(await db.TerminalSettings.AsNoTracking().Where(s => s.TerminalId == terminalId).ToListAsync(ct));
        var pointsByMobile = await Yardsyncs.Infrastructure.DriverApp.DriverRecordService.PointsByMobileAsync(db, terminalId,
            live.Where(a => a.DriverMobile != null).Select(a => a.DriverMobile!).Distinct().ToList(), settings, now, ct);

        var trucks = new List<ControlTruck>();
        foreach (var a in live)
        {
            var stops = stopsByAppointment.GetValueOrDefault(a.AppointmentId) ?? [];
            var current = stops.FirstOrDefault(s => s.StopType != "EXIT" && s.Status is not ("DONE" or "CANCELLED"));
            var stage = a.Status switch
            {
                AppointmentStatus.EnRoute => "ON_THE_WAY",
                AppointmentStatus.Arrived => "AT_GATE",
                _ when current is null => "TO_EXIT",
                _ when current.ArrivedAt is not null => "AT_BLOCK",
                _ => "TO_BLOCK",
            };
            int? inside = a.Status == AppointmentStatus.GateIn && a.GateInAt is { } gin ? (int)Math.Floor((now - gin).TotalMinutes) : null;
            double[]? position = a.LastLatitude is { } lat && a.LastLongitude is { } lon ? [(double)lon, (double)lat] : null;
            var silent = a.Status == AppointmentStatus.GateIn && a.LastPositionAt is { } seen && a.GateInAt is { } gateIn
                         && seen >= gateIn && now - seen > SilentAfter;

            trucks.Add(new ControlTruck(a.AppointmentNo, a.Status, stage, a.Company, a.Plate, a.Driver, a.StartAt, a.EndAt,
                a.Status == AppointmentStatus.EnRoute ? a.CurrentEta : null, a.ArrivedAt, a.GateInAt, inside, position, a.LastPositionAt,
                a.Moves, stops, current?.Sequence, inside > target, silent, a.DriverId,
                a.DriverMobile is null ? 0 : pointsByMobile.GetValueOrDefault(a.DriverMobile),
                Yardsyncs.Application.MasterData.DriverRecordRules.Standing(a.DriverMobile is null ? 0 : pointsByMobile.GetValueOrDefault(a.DriverMobile), settings)));
        }

        // Inside first (longest first), then at the gate, then on the way (soonest first).
        var ordered = trucks
            .OrderBy(t => t.Status switch { AppointmentStatus.GateIn => 0, AppointmentStatus.Arrived => 1, _ => 2 })
            .ThenByDescending(t => t.MinutesInside ?? 0)
            .ThenBy(t => t.Eta ?? t.SlotStart)
            .ToList();

        // Today's times (terminal's day).
        var done = await db.Appointments.AsNoTracking()
            .Where(a => a.TerminalId == terminalId && a.Status == AppointmentStatus.Completed
                        && a.GateOutAt >= dayStart && a.GateInAt != null && a.GateOutAt != null)
            .Select(a => new { a.GateInAt, a.GateOutAt })
            .ToListAsync(ct);
        var todayStops = await db.YardInstructions.AsNoTracking()
            .Where(y => y.ArrivedAt >= dayStart && db.Appointments.Any(a => a.AppointmentId == y.AppointmentId && a.TerminalId == terminalId))
            .Select(y => new
            {
                y.AppointmentId, y.Sequence, y.ArrivedAt, y.CompletedAt,
                GateInAt = db.Appointments.Where(a => a.AppointmentId == y.AppointmentId).Select(a => a.GateInAt).FirstOrDefault(),
            })
            .ToListAsync(ct);

        static double? Average(IEnumerable<double> values)
        {
            var list = values.ToList();
            return list.Count == 0 ? null : Math.Round(list.Average(), 1);
        }

        var stats = new ControlStats(
            OnTheWay: trucks.Count(t => t.Status == AppointmentStatus.EnRoute),
            AtGate: trucks.Count(t => t.Status == AppointmentStatus.Arrived),
            Inside: trucks.Count(t => t.Status == AppointmentStatus.GateIn),
            CompletedToday: done.Count,
            AvgMinutesInside: Average(done.Select(d => (d.GateOutAt!.Value - d.GateInAt!.Value).TotalMinutes)),
            AvgMinutesToBlock: Average(todayStops
                .GroupBy(s => s.AppointmentId)
                .Select(g => g.OrderBy(s => s.Sequence).First())
                .Where(s => s.GateInAt is not null && s.ArrivedAt >= s.GateInAt)
                .Select(s => (s.ArrivedAt!.Value - s.GateInAt!.Value).TotalMinutes)),
            AvgMinutesAtBlock: Average(todayStops
                .Where(s => s.CompletedAt is not null && s.CompletedAt > s.ArrivedAt) // confirmed without the arrival being seen: no time at block
                .Select(s => (s.CompletedAt!.Value - s.ArrivedAt!.Value).TotalMinutes)));

        return new ControlRoomBoard(now, target, stats, ordered, await yard.MapViewAsync(terminalId, ct));
    }
}
