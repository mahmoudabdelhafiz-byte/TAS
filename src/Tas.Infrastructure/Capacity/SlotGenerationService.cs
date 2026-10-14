using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Tas.Application.Capacity;
using Tas.Domain;
using Tas.Domain.Booking;
using Tas.Infrastructure.Notifications;
using Tas.Infrastructure.Persistence;

namespace Tas.Infrastructure.Capacity;

/// <param name="AppointmentsInClosedSlots">Booked appointments whose slot was just closed; their driver and booker are notified.</param>
public sealed record SlotGenerationResult(
    int TerminalId, DateOnly From, DateOnly To, int Created, int Updated, int Closed, int OverCapacity, int AppointmentsInClosedSlots = 0);

/// <summary>Loads a terminal's capacity rules, plans slots and writes them, preserving booked counters.</summary>
public sealed class SlotGenerationService(TasDbContext db, TimeProvider clock, ILogger<SlotGenerationService> logger)
{
    /// <summary>Generates slots for every active terminal from today until its booking horizon.</summary>
    public async Task<IReadOnlyList<SlotGenerationResult>> GenerateAllAsync(CancellationToken ct = default)
    {
        var terminals = await db.Terminals.AsNoTracking().Where(t => t.IsActive).ToListAsync(ct);
        var results = new List<SlotGenerationResult>();

        foreach (var terminal in terminals)
        {
            var timeZone = TimeZoneInfo.FindSystemTimeZoneById(terminal.TimeZone);
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), timeZone).DateTime);
            results.Add(await GenerateAsync(terminal.TerminalId, today, today.AddDays(terminal.BookingHorizonDays), ct));
        }

        return results;
    }

    /// <summary>Generates (or regenerates) slots for one terminal between two local dates, inclusive.</summary>
    public async Task<SlotGenerationResult> GenerateAsync(int terminalId, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var terminal = await db.Terminals.AsNoTracking().SingleOrDefaultAsync(t => t.TerminalId == terminalId, ct)
                       ?? throw new KeyNotFoundException($"Terminal {terminalId} not found.");

        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(terminal.TimeZone);
        var rangeStart = StartOfLocalDayUtc(from, timeZone);
        var rangeEnd = StartOfLocalDayUtc(to.AddDays(1), timeZone);

        var templates = await db.CapacityTemplates.AsNoTracking()
            .Where(t => t.TerminalId == terminalId && t.IsActive)
            .Include(t => t.Slots).ThenInclude(s => s.Quotas)
            .AsSplitQuery()
            .ToListAsync(ct);

        var overrides = await db.CapacityOverrides.AsNoTracking()
            .Where(o => o.TerminalId == terminalId && o.IsActive && o.StartAt < rangeEnd && o.EndAt > rangeStart)
            .ToListAsync(ct);

        var categoryIds = await db.TransactionCategories.AsNoTracking()
            .Where(c => c.TerminalId == terminalId && c.IsActive)
            .Select(c => c.CategoryId)
            .ToListAsync(ct);

        var config = new CapacityConfiguration(terminalId, terminal.TimeZone, terminal.SlotMinutes, templates, overrides, categoryIds);
        var planned = SlotPlanner.Plan(config, from, to);

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var existing = await db.Slots
            .Include(s => s.Categories)
            .Where(s => s.TerminalId == terminalId && s.StartAt >= rangeStart && s.StartAt < rangeEnd)
            .ToListAsync(ct);

        var now = clock.GetUtcNow();
        var openWithBookings = existing.Where(s => !s.IsClosed && s.TotalBooked > 0 && s.EndAt > now).Select(s => s.SlotId).ToHashSet();

        var outcome = SlotReconciler.Reconcile(terminalId, planned, existing, now);
        db.Slots.AddRange(outcome.ToAdd);

        // Bookings are never cancelled, but a booked slot that is now closed needs a new time: tell the driver and the booker.
        var newlyClosed = existing.Where(s => s.IsClosed && openWithBookings.Contains(s.SlotId)).Select(s => s.SlotId).ToList();
        var affected = await db.Appointments.AsNoTracking()
            .Where(a => newlyClosed.Contains(a.SlotId) && (a.Status == AppointmentStatus.Confirmed || a.Status == AppointmentStatus.EnRoute))
            .Select(a => new { a.AppointmentId, a.DriverId, a.CreatedBy })
            .ToListAsync(ct);
        foreach (var a in affected)
            await NotificationOutbox.QueueAsync(db, a.AppointmentId, a.DriverId, a.CreatedBy, NotificationTemplates.SlotClosed, now, ct);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        if (affected.Count > 0)
            logger.LogWarning("Terminal {TerminalId}: {Count} booked appointment(s) are in slots that were closed; drivers and bookers notified", terminalId, affected.Count);
        if (outcome.OverCapacity > 0)
            logger.LogWarning("Terminal {TerminalId}: {Count} slot(s) are booked above their new capacity", terminalId, outcome.OverCapacity);

        logger.LogInformation(
            "Terminal {TerminalId} slots {From}..{To}: {Created} created, {Updated} updated, {Closed} closed",
            terminalId, from, to, outcome.ToAdd.Count, outcome.Updated, outcome.Orphaned);

        return new SlotGenerationResult(terminalId, from, to, outcome.ToAdd.Count, outcome.Updated, outcome.Orphaned, outcome.OverCapacity, affected.Count);
    }

    private static DateTimeOffset StartOfLocalDayUtc(DateOnly date, TimeZoneInfo timeZone)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        while (timeZone.IsInvalidTime(local))
            local = local.AddMinutes(30);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, timeZone), TimeSpan.Zero);
    }
}
