using Microsoft.EntityFrameworkCore;
using Yardsyncs.Application.Capacity;
using Yardsyncs.Infrastructure.Persistence;

namespace Yardsyncs.Infrastructure.Capacity;

/// <param name="ExpectedWaitMinutes">The usual minutes from arrival to gate-in at this hour (null without enough history or when switched off).</param>
/// <param name="GateSpeed">Fast / Normal / Slow against the terminal's target wait (null as above).</param>
public sealed record SlotAvailability(
    long SlotId, DateTimeOffset StartAt, DateTimeOffset EndAt, bool IsClosed, int Available,
    int? ExpectedWaitMinutes = null, string? GateSpeed = null);

/// <summary>Answers "which slots can this company still book for this category on this day?"</summary>
public sealed class AvailabilityQueryService(YardsyncsDbContext db, TimeProvider clock, GateSpeedService? gateSpeed = null)
{
    public async Task<IReadOnlyList<SlotAvailability>> GetAsync(
        int terminalId, DateOnly localDate, string categoryCode, int? companyId, CancellationToken ct = default)
    {
        var terminal = await db.Terminals.AsNoTracking().SingleOrDefaultAsync(t => t.TerminalId == terminalId, ct)
                       ?? throw new KeyNotFoundException($"Terminal {terminalId} not found.");

        var category = await db.TransactionCategories.AsNoTracking()
                           .SingleOrDefaultAsync(c => c.TerminalId == terminalId && c.Code == categoryCode && c.IsActive, ct)
                       ?? throw new KeyNotFoundException($"Category '{categoryCode}' not found for terminal {terminalId}.");

        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(terminal.TimeZone);
        var dayStart = new DateTimeOffset(
            TimeZoneInfo.ConvertTimeToUtc(localDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), timeZone), TimeSpan.Zero);
        var dayEnd = dayStart.AddDays(1);

        var slots = await db.Slots.AsNoTracking()
            .Where(s => s.TerminalId == terminalId && s.StartAt >= dayStart && s.StartAt < dayEnd)
            .OrderBy(s => s.StartAt)
            .Select(s => new
            {
                s.SlotId, s.StartAt, s.EndAt, s.TotalCapacity, s.TotalBooked, s.IsClosed,
                Category = s.Categories
                    .Where(c => c.CategoryId == category.CategoryId)
                    .Select(c => new { c.QuotaCapacity, c.Booked })
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);

        var reservations = await db.CapacityReservations.AsNoTracking()
            .Where(r => r.TerminalId == terminalId && r.IsActive && r.StartAt < dayEnd && r.EndAt > dayStart)
            .ToListAsync(ct);

        var releaseHours = await db.QuotaReleaseRules.AsNoTracking()
            .Where(r => r.TerminalId == terminalId && r.CategoryId == category.CategoryId)
            .Select(r => (int?)r.HoursBefore)
            .FirstOrDefaultAsync(ct);

        var now = clock.GetUtcNow();
        var speed = gateSpeed is null ? null : await gateSpeed.ProfileAsync(terminalId, timeZone, ct);

        return slots.Select(s =>
        {
            var counters = new SlotCounters(
                s.StartAt, s.EndAt, s.TotalCapacity, s.TotalBooked, s.IsClosed,
                s.Category?.QuotaCapacity ?? 0, s.Category?.Booked ?? 0);
            var reserved = AvailabilityCalculator.ReservedUnits(reservations, s.StartAt, now, companyId);
            var available = AvailabilityCalculator.Available(counters, now, reserved, releaseHours);
            var usual = speed?.For(TimeZoneInfo.ConvertTime(s.StartAt, timeZone).DateTime);
            return new SlotAvailability(s.SlotId, s.StartAt, s.EndAt, s.IsClosed, available, usual?.WaitMinutes, usual?.Level.ToString());
        }).ToList();
    }
}
