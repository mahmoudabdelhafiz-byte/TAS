using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yardsyncs.Application.Capacity;
using Yardsyncs.Infrastructure.Persistence;

namespace Yardsyncs.Infrastructure.Capacity;

public sealed record SlotGenerationResult(int TerminalId, DateOnly From, DateOnly To, int Created, int Updated, int Closed, int OverCapacity);

/// <summary>Loads a terminal's capacity rules, plans slots and writes them, preserving booked counters.</summary>
public sealed class SlotGenerationService(YardsyncsDbContext db, TimeProvider clock, ILogger<SlotGenerationService> logger)
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

        var outcome = SlotReconciler.Reconcile(terminalId, planned, existing, clock.GetUtcNow());
        db.Slots.AddRange(outcome.ToAdd);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        if (outcome.OverCapacity > 0)
            logger.LogWarning("Terminal {TerminalId}: {Count} slot(s) are booked above their new capacity", terminalId, outcome.OverCapacity);

        logger.LogInformation(
            "Terminal {TerminalId} slots {From}..{To}: {Created} created, {Updated} updated, {Closed} closed",
            terminalId, from, to, outcome.ToAdd.Count, outcome.Updated, outcome.Orphaned);

        return new SlotGenerationResult(terminalId, from, to, outcome.ToAdd.Count, outcome.Updated, outcome.Orphaned, outcome.OverCapacity);
    }

    private static DateTimeOffset StartOfLocalDayUtc(DateOnly date, TimeZoneInfo timeZone)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        while (timeZone.IsInvalidTime(local))
            local = local.AddMinutes(30);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, timeZone), TimeSpan.Zero);
    }
}
