using Microsoft.EntityFrameworkCore;
using Tas.Application.Capacity;
using Tas.Infrastructure.Persistence;

namespace Tas.Infrastructure.Capacity;

public sealed record SlotAvailability(long SlotId, DateTimeOffset StartAt, DateTimeOffset EndAt, bool IsClosed, int Available);

/// <summary>Answers "which slots can this company still book for this category on this day?"</summary>
public sealed class AvailabilityQueryService(TasDbContext db, TimeProvider clock)
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

        return slots.Select(s =>
        {
            var counters = new SlotCounters(
                s.StartAt, s.EndAt, s.TotalCapacity, s.TotalBooked, s.IsClosed,
                s.Category?.QuotaCapacity ?? 0, s.Category?.Booked ?? 0);
            var reserved = AvailabilityCalculator.ReservedUnits(reservations, s.StartAt, now, companyId);
            var available = AvailabilityCalculator.Available(counters, now, reserved, releaseHours);
            return new SlotAvailability(s.SlotId, s.StartAt, s.EndAt, s.IsClosed, available);
        }).ToList();
    }
}
