using Microsoft.EntityFrameworkCore;
using Yardsyncs.Application.Gate;
using Yardsyncs.Domain.Booking;
using Yardsyncs.Domain.Gate;
using Yardsyncs.Infrastructure.Persistence;

namespace Yardsyncs.Infrastructure.Gate;

public sealed record GateTicketMove(string Category, string CategoryAr, string? ContainerNo, string? IsoType, string? Reference, IReadOnlyList<string> Seals,
    SealStatus SealStatus = SealStatus.NotChecked);

/// <summary>Damage seen at gate-in: from the inspection, or as the cameras reported it (kind unknown).</summary>
public sealed record GateTicketDamage(string? ContainerNo, DamageSide? Side, DamageKind? Kind, DamageSeverity? Severity, string? Note);

/// <summary>
/// The gate ticket (equipment interchange receipt) for a visit: who and what came through, when and at which lanes,
/// seals and damage seen at the gate, and the TOS's visit reference once recorded.
/// </summary>
public sealed record GateTicketView(
    string TerminalName,
    string AppointmentNo,
    AppointmentStatus Status,
    string CompanyName,
    string? TruckPlate,
    string? DriverName,
    string SlotLocal,
    DateTimeOffset? ArrivedAt,
    DateTimeOffset? GateInAt,
    DateTimeOffset? GateOutAt,
    string? InLane,
    string? OutLane,
    string? TosVisitReference,
    IReadOnlyList<GateTicketMove> Moves,
    IReadOnlyList<GateTicketDamage> Damage,
    decimal? WeightKg);

/// <summary>Builds the gate ticket for the gate screen, the lane printer and the driver.</summary>
public sealed class GateTicketService(YardsyncsDbContext db, Yardsyncs.Infrastructure.Booking.AppointmentQueryService queries)
{
    public async Task<GateTicketView?> TicketAsync(int terminalId, string appointmentNo, CancellationToken ct = default)
    {
        var number = appointmentNo.Trim().ToUpperInvariant();
        var dto = await queries.GetAsync(terminalId, number, ct);
        if (dto is null)
            return null;
        var a = await db.Appointments.AsNoTracking().Where(x => x.TerminalId == terminalId && x.AppointmentNo == number)
            .Select(x => new
            {
                x.AppointmentId, x.ArrivedAt, x.GateInAt, x.GateOutAt,
                Items = x.Items.OrderBy(i => i.ItemId).Select(i => new
                {
                    i.Category.NameEn, i.Category.NameAr, i.ContainerNo, i.IsoType,
                    Reference = i.BookingRef ?? i.EdoRef ?? (i.CargoAllocation != null ? i.CargoAllocation.CargoRef : null),
                }).ToList(),
            }).SingleAsync(ct);
        var terminal = await db.Terminals.AsNoTracking().Where(t => t.TerminalId == terminalId).Select(t => t.Name).SingleAsync(ct);

        var laneEvents = await db.GateEvents.AsNoTracking()
            .Where(e => e.AppointmentId == a.AppointmentId && e.LaneId != null && (e.EventType == GateEventType.GateIn || e.EventType == GateEventType.Matched || e.EventType == GateEventType.GateOut))
            .Join(db.GateLanes, e => e.LaneId, l => l.LaneId, (e, l) => new { e.EventType, e.OccurredAt, l.LaneCode })
            .ToListAsync(ct);
        var inLane = laneEvents.Where(e => e.EventType != GateEventType.GateOut).OrderBy(e => e.OccurredAt).Select(e => e.LaneCode).LastOrDefault();
        var outLane = laneEvents.Where(e => e.EventType == GateEventType.GateOut).Select(e => e.LaneCode).LastOrDefault();

        var reads = (await db.LaneDeviceReads.AsNoTracking()
                .Where(r => r.TransactionId != null && db.LaneTransactions.Any(t => t.TransactionId == r.TransactionId && t.AppointmentId == a.AppointmentId))
                .OrderBy(r => r.ReceivedAt).ToListAsync(ct))
            .Select(DeviceService.Normalized).ToList();
        var seals = reads.SelectMany(r => r.Seals).Distinct().ToList();
        var inspections = await db.GateInspections.AsNoTracking().Include(i => i.Damage)
            .Where(i => i.AppointmentId == a.AppointmentId && i.Stage == InspectionStage.In).ToListAsync(ct);
        var byContainer = inspections.ToDictionary(i => i.ContainerNo);
        var damage = inspections.Count > 0
            ? inspections.SelectMany(i => i.Damage.OrderBy(d => d.DamageId).Select(d =>
                new GateTicketDamage(i.ContainerNo.Length == 0 ? null : i.ContainerNo, d.Side, d.Kind, d.Severity, d.Note))).ToList()
            : reads.SelectMany(r => r.Damage).Select(d => new GateTicketDamage(null, InspectionRules.SideFrom(d.Side), null, null, d.Note)).ToList();
        var weight = reads.Select(r => r.WeightKg).LastOrDefault(w => w is not null);

        var visit = await db.TosGateMessages.AsNoTracking()
            .Where(m => m.AppointmentId == a.AppointmentId && m.Stage == TosGateStageCode.GateIn && m.Status == TosMessageStatus.Sent)
            .Select(m => m.TosReference).SingleOrDefaultAsync(ct);

        return new GateTicketView(
            terminal, dto.AppointmentNo, dto.Status, dto.CompanyName, dto.TruckPlate, dto.DriverName, dto.SlotLocal,
            a.ArrivedAt, a.GateInAt, a.GateOutAt, inLane, outLane, visit,
            a.Items.Select(i => byContainer.TryGetValue(i.ContainerNo ?? "", out var seen)
                ? new GateTicketMove(i.NameEn, i.NameAr, i.ContainerNo, i.IsoType, i.Reference, seen.SealSeen is null ? [] : [seen.SealSeen], seen.SealStatus)
                : new GateTicketMove(i.NameEn, i.NameAr, i.ContainerNo, i.IsoType, i.Reference, i.ContainerNo is null ? [] : seals)).ToList(),
            damage, weight);
    }
}
