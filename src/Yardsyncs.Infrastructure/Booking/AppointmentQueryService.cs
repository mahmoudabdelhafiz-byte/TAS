using Yardsyncs.Application.Languages;
using Microsoft.EntityFrameworkCore;
using Yardsyncs.Application.Booking;
using Yardsyncs.Domain;
using Yardsyncs.Domain.Booking;
using Yardsyncs.Infrastructure.Persistence;

namespace Yardsyncs.Infrastructure.Booking;

/// <summary>Reads appointments with their live traffic light.</summary>
public sealed class AppointmentQueryService(YardsyncsDbContext db, TimeProvider clock)
{
    public Task<AppointmentDto?> GetAsync(int terminalId, string appointmentNo, CancellationToken ct = default) =>
        GetAsync(terminalId, appointmentNo, companyId: null, ct);

    /// <param name="companyId">When set, appointments of other companies are treated as not found.</param>
    public async Task<AppointmentDto?> GetAsync(int terminalId, string appointmentNo, int? companyId, CancellationToken ct = default)
    {
        var number = appointmentNo.Trim().ToUpperInvariant();
        var query = WithDetails().Where(a => a.TerminalId == terminalId && a.AppointmentNo == number);
        if (companyId is not null)
            query = query.Where(a => a.CompanyId == companyId);

        var appointment = await query.SingleOrDefaultAsync(ct);
        if (appointment is null)
            return null;

        var (terminal, settings) = await LoadTerminalAsync(terminalId, ct);
        return ToDto(appointment, terminal, settings, clock.GetUtcNow());
    }

    /// <summary>Appointments whose slot falls on a local day, optionally for one company.</summary>
    public async Task<IReadOnlyList<AppointmentDto>> ListAsync(
        int terminalId, DateOnly localDate, int? companyId, CancellationToken ct = default)
    {
        var (terminal, settings) = await LoadTerminalAsync(terminalId, ct);
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(terminal.TimeZone);
        var dayStart = new DateTimeOffset(
            TimeZoneInfo.ConvertTimeToUtc(localDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), timeZone), TimeSpan.Zero);
        var dayEnd = dayStart.AddDays(1);

        var query = WithDetails().Where(a => a.TerminalId == terminalId && a.Slot.StartAt >= dayStart && a.Slot.StartAt < dayEnd);
        if (companyId is not null)
            query = query.Where(a => a.CompanyId == companyId);

        var appointments = await query.OrderBy(a => a.Slot.StartAt).ThenBy(a => a.AppointmentId).ToListAsync(ct);
        var now = clock.GetUtcNow();
        return appointments.Select(a => ToDto(a, terminal, settings, now)).ToList();
    }

    /// <summary>How far back and ahead a search looks, and how many it returns.</summary>
    public static readonly TimeSpan SearchWindow = TimeSpan.FromDays(90);
    public const int SearchLimit = 50;

    /// <summary>
    /// Appointments on any day (90 days back and ahead) by appointment number, container number, truck plate (Arabic or
    /// Latin) or booking / EDO / cargo reference; newest slot first.
    /// </summary>
    public async Task<IReadOnlyList<AppointmentDto>> SearchAsync(int terminalId, string text, int? companyId, CancellationToken ct = default)
    {
        var q = text.Trim().ToUpperInvariant();
        if (q.Length < 3)
            return [];
        var (terminal, settings) = await LoadTerminalAsync(terminalId, ct);
        var now = clock.GetUtcNow();
        var from = now - SearchWindow;
        var to = now + SearchWindow;
        var container = ContainerNumber.Normalize(q);
        var plate = Yardsyncs.Application.MasterData.PlateNumber.Normalize(q);

        var query = WithDetails().Where(a => a.TerminalId == terminalId && a.Slot.StartAt >= from && a.Slot.StartAt <= to
            && (a.AppointmentNo == q
                || (plate.Length > 0 && a.Truck != null && a.Truck.PlateNormalized == plate)
                || a.Items.Any(i => (container.Length > 0 && i.ContainerNo == container)
                                    || i.BookingRef == q || i.EdoRef == q
                                    || (i.CargoAllocation != null && i.CargoAllocation.CargoRef == q))));
        if (companyId is not null)
            query = query.Where(a => a.CompanyId == companyId);

        var appointments = await query.OrderByDescending(a => a.Slot.StartAt).ThenByDescending(a => a.AppointmentId).Take(SearchLimit).ToListAsync(ct);
        return appointments.Select(a => ToDto(a, terminal, settings, now)).ToList();
    }

    private IQueryable<Appointment> WithDetails() =>
        db.Appointments.AsNoTracking()
            .Include(a => a.Slot)
            .Include(a => a.Company)
            .Include(a => a.Truck)
            .Include(a => a.Driver)
            .Include(a => a.Items).ThenInclude(i => i.Category)
            .Include(a => a.Items).ThenInclude(i => i.CargoAllocation)
            .Include(a => a.Items).ThenInclude(i => i.ValidationResults)
            .AsSplitQuery();

    internal async Task<(Terminal Terminal, TerminalSettings Settings)> LoadTerminalAsync(int terminalId, CancellationToken ct)
    {
        var terminal = await db.Terminals.AsNoTracking().SingleOrDefaultAsync(t => t.TerminalId == terminalId, ct)
                       ?? throw new KeyNotFoundException($"Terminal {terminalId} not found.");
        var settings = TerminalSettings.From(
            await db.TerminalSettings.AsNoTracking().Where(s => s.TerminalId == terminalId).ToListAsync(ct));
        return (terminal, settings);
    }

    /// <summary>The driver's traffic light for an appointment loaded with its slot, items and validation results.</summary>
    internal static (TrafficLight Light, string Reason) Traffic(Appointment a, TerminalSettings settings, TimeZoneInfo timeZone, DateTimeOffset now) =>
        TrafficLightCalculator.Calculate(
            a.Status,
            a.Items.OrderBy(i => i.ItemId).Select((i, n) => new ItemCheckState(
                    $"Item {n + 1}",
                    i.ValidationStatus,
                    i.ValidationResults.Where(v => v.Result != CheckResult.Pass).OrderByDescending(v => v.Result).Select(v => v.Message).FirstOrDefault()))
                .ToList(),
            a.Slot.StartAt, a.Slot.EndAt, now, settings, timeZone);

    internal static AppointmentDto ToDto(Appointment a, Terminal terminal, TerminalSettings settings, DateTimeOffset now)
    {
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(terminal.TimeZone);
        var items = a.Items.OrderBy(i => i.ItemId).ToList();
        var (light, reason) = Traffic(a, settings, timeZone, now);

        var localStart = TimeZoneInfo.ConvertTime(a.Slot.StartAt, timeZone);
        var localEnd = TimeZoneInfo.ConvertTime(a.Slot.EndAt, timeZone);

        return new AppointmentDto(
            a.AppointmentNo,
            a.Status,
            a.SlotId,
            a.Slot.StartAt,
            a.Slot.EndAt,
            $"{localStart:yyyy-MM-dd HH:mm}–{localEnd:HH:mm}",
            a.CompanyId,
            a.Company.Name,
            a.TruckId,
            a.Truck?.PlateNumber,
            a.DriverId,
            a.Driver?.FullName,
            a.IsDual,
            light,
            ServerText.T(reason),
            a.QrToken,
            items.Select((i, n) => new AppointmentItemDto(
                i.Category.Code,
                i.ContainerNo,
                i.IsoType,
                i.BookingRef,
                i.EdoRef,
                i.CargoAllocation?.CargoRef,
                i.PlannedTonnage,
                i.ValidationStatus,
                i.ValidationResults
                    .OrderBy(v => v.ValidationId)
                    .Select(v => new CheckDto(n + 1, v.CheckType, v.Result, ServerText.TOrNull(v.Message), v.Source))
                    .ToList(),
                i.SealNo,
                i.Vin,
                i.VehicleDesc)).ToList(),
            a.CreatedAt,
            a.Status == AppointmentStatus.EnRoute ? a.CurrentEta : null);
    }
}
