using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Yardsyncs.Application.Booking;
using Yardsyncs.Application.Capacity;
using Yardsyncs.Application.MasterData;
using Yardsyncs.Domain.Booking;
using Yardsyncs.Infrastructure.Booking;
using Yardsyncs.Infrastructure.Persistence;

namespace Yardsyncs.Infrastructure.Zone;

/// <summary>
/// What the zone system tells Yardsyncs (POST /api/zone/events). Type: YARD_ENTERED (the truck is in the zone's
/// marshalling yard), YARD_RELEASED (released from the yard towards the terminal), PERMIT_REVOKED, CLEARANCE_CHANGED
/// (customs status of a container changed: its visits are re-checked at once). The visit is found by appointment number,
/// else by plate (a live visit within a day either side).
/// </summary>
public sealed record ZoneEventInput(string? Type, string? Terminal, string? AppointmentNo, string? Plate, string? ContainerNo, string? Message);

/// <param name="Visits">The appointment numbers the event was applied to.</param>
public sealed record ZoneEventResult(string Type, IReadOnlyList<string> Visits);

public sealed class ZoneEventService(
    YardsyncsDbContext db, RevalidationService revalidation, IOptions<NafithOptions> options, TimeProvider clock, ILogger<ZoneEventService> logger)
{
    public static readonly IReadOnlyList<string> Types = ["YARD_ENTERED", "YARD_RELEASED", "PERMIT_REVOKED", "CLEARANCE_CHANGED"];

    /// <summary>The key the zone system sends, compared in constant time; false when no key is set (inbound off).</summary>
    public bool KeyMatches(string? key)
    {
        var expected = options.Value.InboundKey;
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(key))
            return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(expected));
    }

    public async Task<AdminResult<ZoneEventResult>> ApplyAsync(ZoneEventInput input, CancellationToken ct = default)
    {
        var type = (input.Type ?? "").Trim().ToUpperInvariant();
        if (!Types.Contains(type))
            return AdminResult<ZoneEventResult>.Invalid($"Type must be one of {string.Join(", ", Types)}.");
        var code = (input.Terminal ?? "").Trim();
        var terminal = await db.Terminals.AsNoTracking().Where(t => t.Code == code).Select(t => new { t.TerminalId, t.Code }).SingleOrDefaultAsync(ct);
        if (terminal is null)
            return AdminResult<ZoneEventResult>.Invalid("Terminal: give the Yardsyncs terminal code.");
        var now = clock.GetUtcNow();

        if (type == "CLEARANCE_CHANGED")
        {
            var container = ContainerNumber.Normalize(input.ContainerNo ?? "");
            if (container.Length == 0)
                return AdminResult<ZoneEventResult>.Invalid("ContainerNo is needed for CLEARANCE_CHANGED.");
            var visits = await db.Appointments.AsNoTracking()
                .Where(a => a.TerminalId == terminal.TerminalId
                            && (a.Status == AppointmentStatus.Confirmed || a.Status == AppointmentStatus.EnRoute || a.Status == AppointmentStatus.Arrived)
                            && a.Items.Any(i => i.ContainerNo == container))
                .Select(a => new { a.AppointmentId, a.AppointmentNo })
                .ToListAsync(ct);
            foreach (var visit in visits)
            {
                try
                {
                    await revalidation.RevalidateAsync(visit.AppointmentId, "zone", ct);
                }
                catch (Yardsyncs.Application.Tos.TosUnavailableException e)
                {
                    logger.LogWarning(e, "Re-check of {Appointment} after a zone clearance change: TOS unreachable", visit.AppointmentNo);
                }
            }
            return AdminResult<ZoneEventResult>.Ok(new ZoneEventResult(type, visits.Select(v => v.AppointmentNo).ToList()));
        }

        var appointment = await FindAsync(terminal.TerminalId, input, now, ct);
        if (appointment is null)
            return AdminResult<ZoneEventResult>.NotFound("No live visit found for this appointment number or plate.");

        var note = type switch
        {
            "YARD_ENTERED" => "In the zone's truck yard",
            "YARD_RELEASED" => "Released from the zone's truck yard to the terminal",
            _ => "Entry permit withdrawn in the zone system",
        };
        if (!string.IsNullOrWhiteSpace(input.Message))
            note += $": {input.Message.Trim()}";
        db.AppointmentStatusHistory.Add(new AppointmentStatusHistory
        {
            AppointmentId = appointment.Value.Id,
            FromStatus = appointment.Value.Status,
            ToStatus = appointment.Value.Status,
            Reason = note.Length > 300 ? note[..300] : note,
            ChangedAt = now,
        });
        // In the zone's yard the truck is on its way: the control room counts it as coming.
        if (type == "YARD_ENTERED" && appointment.Value.Status == AppointmentStatus.Confirmed)
            await db.Appointments.Where(a => a.AppointmentId == appointment.Value.Id && a.Status == AppointmentStatus.Confirmed)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, AppointmentStatus.EnRoute), ct);
        await db.SaveChangesAsync(ct);
        return AdminResult<ZoneEventResult>.Ok(new ZoneEventResult(type, [appointment.Value.No]));
    }

    private async Task<(long Id, string No, AppointmentStatus Status)?> FindAsync(int terminalId, ZoneEventInput input, DateTimeOffset now, CancellationToken ct)
    {
        var live = db.Appointments.AsNoTracking().Where(a => a.TerminalId == terminalId
            && (a.Status == AppointmentStatus.Confirmed || a.Status == AppointmentStatus.EnRoute || a.Status == AppointmentStatus.Arrived));
        if (!string.IsNullOrWhiteSpace(input.AppointmentNo))
        {
            var number = input.AppointmentNo.Trim().ToUpperInvariant();
            var byNumber = await live.Where(a => a.AppointmentNo == number)
                .Select(a => new { a.AppointmentId, a.AppointmentNo, a.Status }).SingleOrDefaultAsync(ct);
            return byNumber is null ? null : (byNumber.AppointmentId, byNumber.AppointmentNo, byNumber.Status);
        }
        var plate = PlateNumber.Normalize(input.Plate);
        if (plate.Length == 0)
            return null;
        var from = now.AddHours(-24);
        var to = now.AddHours(24);
        var candidates = await live
            .Where(a => a.Truck != null && a.Truck.PlateNormalized == plate && a.Slot.StartAt >= from && a.Slot.StartAt <= to)
            .Select(a => new { a.AppointmentId, a.AppointmentNo, a.Status, a.Slot.StartAt })
            .ToListAsync(ct);
        var nearest = candidates.OrderBy(c => Math.Abs((c.StartAt - now).TotalMinutes)).FirstOrDefault();
        return nearest is null ? null : (nearest.AppointmentId, nearest.AppointmentNo, nearest.Status);
    }
}
