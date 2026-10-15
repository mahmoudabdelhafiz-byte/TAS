using Microsoft.EntityFrameworkCore;
using Tas.Application.Auth;
using Tas.Application.Booking;
using Tas.Domain.Booking;
using Tas.Infrastructure.Booking;
using Tas.Infrastructure.Persistence;

namespace Tas.Infrastructure.DriverApp;

public sealed record DriverMove(string Category, string CategoryEn, string CategoryAr, string? ContainerNo, string? BookingRef, string? CargoRef);

/// <param name="UtcOffsetMinutes">The terminal's UTC offset at the slot, so the app shows terminal time without a time-zone database.</param>
/// <param name="GateCode">What the lane reads (QR) — only while the appointment can still pass the gate.</param>
public sealed record DriverAppointment(
    string AppointmentNo,
    int TerminalId,
    string TerminalName,
    string TimeZone,
    int UtcOffsetMinutes,
    DateTimeOffset SlotStart,
    DateTimeOffset SlotEnd,
    AppointmentStatus Status,
    TrafficLight TrafficLight,
    string TrafficReason,
    string CompanyName,
    string? TruckPlate,
    IReadOnlyList<DriverMove> Moves,
    string? GateCode,
    DateTimeOffset? Eta);

/// <summary>What the driver app reads and does: the driver's appointments (by mobile number, across companies) and start trip.</summary>
public sealed class DriverAppService(TasDbContext db, AppointmentQueryService queries, RevalidationService revalidation, TimeProvider clock)
{
    public async Task<DriverProfile?> ProfileAsync(CurrentDriver driver, CancellationToken ct = default)
    {
        var rows = await db.Drivers.AsNoTracking().Where(d => d.Mobile == driver.Mobile && !d.IsBlocked).ToListAsync(ct);
        var me = rows.FirstOrDefault(d => d.DriverId == driver.DriverId) ?? rows.FirstOrDefault();
        if (me is null)
            return null;
        var companyIds = rows.Select(d => d.CompanyId).Distinct().ToList();
        var companies = await db.Companies.AsNoTracking().Where(c => companyIds.Contains(c.CompanyId)).Select(c => c.Name).ToListAsync(ct);
        return new DriverProfile(me.DriverId, me.FullName, driver.Mobile, me.PreferredLanguage.Trim(), companies);
    }

    /// <summary>Upcoming and live appointments, plus the ones finished in the last 12 hours.</summary>
    public async Task<IReadOnlyList<DriverAppointment>> ListAsync(CurrentDriver driver, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var since = now.AddHours(-12);
        var rows = await Mine(driver)
            .Where(a => a.Status == AppointmentStatus.Confirmed || a.Status == AppointmentStatus.EnRoute
                        || a.Status == AppointmentStatus.Arrived || a.Status == AppointmentStatus.GateIn
                        || (a.Slot.EndAt >= since && (a.Status == AppointmentStatus.Completed || a.Status == AppointmentStatus.Cancelled
                                                       || a.Status == AppointmentStatus.NoShow || a.Status == AppointmentStatus.RejectedAtGate)))
            .OrderBy(a => a.Slot.StartAt)
            .Select(a => new { a.TerminalId, a.AppointmentNo })
            .Take(30)
            .ToListAsync(ct);

        var result = new List<DriverAppointment>();
        foreach (var row in rows)
        {
            if (await ViewAsync(row.TerminalId, row.AppointmentNo, ct) is { } view)
                result.Add(view);
        }
        return result;
    }

    public async Task<DriverAppointment?> GetAsync(CurrentDriver driver, string appointmentNo, CancellationToken ct = default)
    {
        var number = appointmentNo.Trim().ToUpperInvariant();
        var row = await Mine(driver).Where(a => a.AppointmentNo == number).Select(a => new { a.TerminalId }).SingleOrDefaultAsync(ct);
        return row is null ? null : await ViewAsync(row.TerminalId, number, ct);
    }

    public async Task<(BookingOutcome Outcome, DriverAppointment? View)> StartTripAsync(CurrentDriver driver, string appointmentNo, CancellationToken ct = default)
    {
        var number = appointmentNo.Trim().ToUpperInvariant();
        var row = await Mine(driver).Where(a => a.AppointmentNo == number).Select(a => new { a.AppointmentId, a.TerminalId }).SingleOrDefaultAsync(ct);
        if (row is null)
            return (BookingOutcome.NotFound($"Appointment {number} not found."), null);

        var outcome = await revalidation.StartTripForDriverAsync(row.AppointmentId, ct);
        return (outcome, outcome.Appointment is null ? null : await ViewAsync(row.TerminalId, number, ct));
    }

    /// <summary>Appointments of every driver record with this mobile number (one person, several companies).</summary>
    private IQueryable<Appointment> Mine(CurrentDriver driver) =>
        db.Appointments.AsNoTracking()
            .Where(a => a.DriverId != null && db.Drivers.Any(d => d.DriverId == a.DriverId && d.Mobile == driver.Mobile && !d.IsBlocked));

    private async Task<DriverAppointment?> ViewAsync(int terminalId, string number, CancellationToken ct)
    {
        var dto = await queries.GetAsync(terminalId, number, ct);
        if (dto is null)
            return null;
        var terminal = await db.Terminals.AsNoTracking().Where(t => t.TerminalId == terminalId).Select(t => new { t.Name, t.TimeZone }).SingleAsync(ct);
        var names = await db.TransactionCategories.AsNoTracking().Where(c => c.TerminalId == terminalId)
            .ToDictionaryAsync(c => c.Code, c => (c.NameEn, c.NameAr), ct);

        var live = dto.Status is AppointmentStatus.Confirmed or AppointmentStatus.EnRoute or AppointmentStatus.Arrived;
        return new DriverAppointment(
            dto.AppointmentNo, terminalId, terminal.Name, terminal.TimeZone,
            (int)TimeZoneInfo.FindSystemTimeZoneById(terminal.TimeZone).GetUtcOffset(dto.SlotStart).TotalMinutes, dto.SlotStart, dto.SlotEnd, dto.Status,
            dto.TrafficLight, dto.TrafficReason, dto.CompanyName, dto.TruckPlate,
            dto.Items.Select(i => new DriverMove(
                i.Category,
                names.TryGetValue(i.Category, out var n) ? n.NameEn : i.Category,
                names.TryGetValue(i.Category, out var m) ? m.NameAr : i.Category,
                i.ContainerNo, i.BookingRef, i.CargoRef)).ToList(),
            live ? dto.QrToken : null,
            dto.Eta);
    }
}
