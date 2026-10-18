using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Yardsyncs.Application.Capacity;
using Yardsyncs.Domain;
using Yardsyncs.Infrastructure.Persistence;

namespace Yardsyncs.Infrastructure.Capacity;

/// <summary>The gate speed profiles, kept for half an hour per terminal (booking asks for them at every page).</summary>
public sealed class GateSpeedCache
{
    public static readonly TimeSpan KeepFor = TimeSpan.FromMinutes(30);

    internal readonly ConcurrentDictionary<int, (DateTimeOffset At, GateSpeedProfile? Profile)> Entries = new();

    /// <summary>Forget a terminal's profile (its settings changed).</summary>
    public void Forget(int terminalId) => Entries.TryRemove(terminalId, out _);
}

/// <summary>
/// How quickly each hour usually gets through the gate at a terminal, from its own arrivals and gate-ins over the
/// history weeks of its settings (AI-optimised slots, step 2). Null when the terminal switched it off.
/// </summary>
public sealed class GateSpeedService(YardsyncsDbContext db, GateSpeedCache cache, TimeProvider clock)
{
    public async Task<GateSpeedProfile?> ProfileAsync(int terminalId, TimeZoneInfo timeZone, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        if (cache.Entries.TryGetValue(terminalId, out var entry) && now - entry.At < GateSpeedCache.KeepFor)
            return entry.Profile;

        var settings = TerminalSettings.From(await db.TerminalSettings.AsNoTracking().Where(s => s.TerminalId == terminalId).ToListAsync(ct));
        GateSpeedProfile? profile = null;
        if (settings.SlotGateSpeed)
        {
            var from = now.AddDays(-7 * settings.SlotHistoryWeeks);
            var visits = await db.Appointments.AsNoTracking()
                .Where(a => a.TerminalId == terminalId && a.Slot.StartAt >= from && a.Slot.StartAt < now
                            && a.ArrivedAt != null && a.GateInAt != null)
                .Select(a => new { a.Slot.StartAt, a.ArrivedAt, a.GateInAt })
                .ToListAsync(ct);
            profile = GateSpeedProfile.From(
                visits.Select(v => new SpeedVisit(v.StartAt, v.ArrivedAt!.Value, v.GateInAt!.Value)).ToList(),
                timeZone, settings.SlotTargetWaitMinutes);
        }
        cache.Entries[terminalId] = (now, profile);
        return profile;
    }
}
