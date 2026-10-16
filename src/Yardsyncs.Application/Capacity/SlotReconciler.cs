using Yardsyncs.Domain.Capacity;

namespace Yardsyncs.Application.Capacity;

public sealed record ReconcileOutcome(IReadOnlyList<Slot> ToAdd, int Updated, int Orphaned, int OverCapacity);

/// <summary>
/// Applies a freshly planned set of slots to the slots already stored for the same period.
/// Booked counters are never touched; when capacity drops below what is booked the slot is
/// flagged <see cref="Slot.OverCapacity"/> instead of cancelling bookings.
/// Stored slots that are no longer planned (e.g. a window was removed) are closed, not deleted.
/// </summary>
public static class SlotReconciler
{
    public static ReconcileOutcome Reconcile(
        int terminalId, IReadOnlyList<PlannedSlot> planned, IReadOnlyList<Slot> existing, DateTimeOffset now)
    {
        var byStart = existing.ToDictionary(s => s.StartAt);
        var plannedStarts = new HashSet<DateTimeOffset>();
        var toAdd = new List<Slot>();
        var updated = 0;

        foreach (var p in planned)
        {
            plannedStarts.Add(p.StartAt);

            if (byStart.TryGetValue(p.StartAt, out var slot))
            {
                Apply(slot, p, now);
                updated++;
            }
            else
            {
                toAdd.Add(new Slot
                {
                    TerminalId = terminalId,
                    StartAt = p.StartAt,
                    EndAt = p.EndAt,
                    TotalCapacity = p.TotalCapacity,
                    IsClosed = p.IsClosed,
                    GeneratedAt = now,
                    Categories = p.CategoryQuotas
                        .Select(kv => new SlotCategoryCapacity { CategoryId = kv.Key, QuotaCapacity = kv.Value })
                        .ToList(),
                });
            }
        }

        var orphaned = 0;
        foreach (var slot in existing.Where(s => !plannedStarts.Contains(s.StartAt)))
        {
            slot.IsClosed = true;
            slot.TotalCapacity = 0;
            foreach (var c in slot.Categories)
                c.QuotaCapacity = 0;
            slot.OverCapacity = slot.HasOverCapacity();
            slot.GeneratedAt = now;
            orphaned++;
        }

        var overCapacity = existing.Count(s => s.OverCapacity);
        return new ReconcileOutcome(toAdd, updated, orphaned, overCapacity);
    }

    private static void Apply(Slot slot, PlannedSlot p, DateTimeOffset now)
    {
        slot.EndAt = p.EndAt;
        slot.TotalCapacity = p.TotalCapacity;
        slot.IsClosed = p.IsClosed;
        slot.GeneratedAt = now;

        foreach (var (categoryId, quota) in p.CategoryQuotas)
        {
            var row = slot.Categories.FirstOrDefault(c => c.CategoryId == categoryId);
            if (row is null)
                slot.Categories.Add(new SlotCategoryCapacity { SlotId = slot.SlotId, CategoryId = categoryId, QuotaCapacity = quota });
            else
                row.QuotaCapacity = quota;
        }

        // Categories that are no longer active get no capacity.
        foreach (var row in slot.Categories.Where(c => !p.CategoryQuotas.ContainsKey(c.CategoryId)))
            row.QuotaCapacity = 0;

        slot.OverCapacity = slot.HasOverCapacity();
    }
}
