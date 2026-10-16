using Microsoft.EntityFrameworkCore;
using Yardsyncs.Infrastructure.Persistence;

namespace Yardsyncs.Infrastructure.Capacity;

/// <summary>
/// Race-safe capacity counters. Each UPDATE only succeeds while capacity remains, and is atomic
/// on its row, so two bookings racing for the last unit cannot both succeed.
///
/// This is the only hand-written SQL in the capacity area; keep it here so another database
/// provider can be supported by swapping this class.
/// </summary>
public sealed class BookingCounterStore(YardsyncsDbContext db)
{
    /// <summary>
    /// Consumes one unit of slot total (one truck) plus the given units per category.
    /// Must run inside a transaction: if it returns false, roll the transaction back.
    /// </summary>
    /// <param name="reservedUnits">Units held back from this booker (see AvailabilityCalculator.ReservedUnits).</param>
    /// <param name="releasedCategoryIds">Categories whose quota has been released to the shared pool.</param>
    public async Task<bool> TryConsumeAsync(
        long slotId,
        IReadOnlyDictionary<int, int> unitsByCategory,
        int reservedUnits,
        IReadOnlySet<int> releasedCategoryIds,
        CancellationToken ct = default)
    {
        EnsureTransaction();

        var rows = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             UPDATE slot
                SET total_booked = total_booked + 1
              WHERE slot_id = {slotId}
                AND NOT is_closed
                AND total_booked + 1 <= total_capacity - {reservedUnits}
             """, ct);
        if (rows == 0)
            return false;

        foreach (var (categoryId, units) in unitsByCategory)
        {
            var released = releasedCategoryIds.Contains(categoryId);
            rows = await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 UPDATE slot_category_capacity
                    SET booked = booked + {units}
                  WHERE slot_id = {slotId}
                    AND category_id = {categoryId}
                    AND ({released} OR booked + {units} <= quota_capacity)
                 """, ct);
            if (rows == 0)
                return false;
        }

        return true;
    }

    /// <summary>Gives capacity back after a cancellation or no-show.</summary>
    public async Task ReleaseAsync(long slotId, IReadOnlyDictionary<int, int> unitsByCategory, CancellationToken ct = default)
    {
        EnsureTransaction();

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE slot SET total_booked = GREATEST(0, total_booked - 1) WHERE slot_id = {slotId}", ct);

        foreach (var (categoryId, units) in unitsByCategory)
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 UPDATE slot_category_capacity
                    SET booked = GREATEST(0, booked - {units})
                  WHERE slot_id = {slotId} AND category_id = {categoryId}
                 """, ct);
        }
    }

    /// <summary>Draws trips down from a multipurpose cargo allocation. False when it is not open or has no trips left.</summary>
    public async Task<bool> TryConsumeCargoTripsAsync(int cargoAllocationId, int trips, CancellationToken ct = default)
    {
        EnsureTransaction();

        var rows = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             UPDATE cargo_allocation
                SET booked_trips = booked_trips + {trips}
              WHERE cargo_allocation_id = {cargoAllocationId}
                AND status = 'OPEN'
                AND (total_trips IS NULL OR booked_trips + {trips} <= total_trips)
             """, ct);
        return rows > 0;
    }

    public async Task ReleaseCargoTripsAsync(int cargoAllocationId, int trips, CancellationToken ct = default)
    {
        EnsureTransaction();

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             UPDATE cargo_allocation
                SET booked_trips = GREATEST(0, booked_trips - {trips})
              WHERE cargo_allocation_id = {cargoAllocationId}
             """, ct);
    }

    private void EnsureTransaction()
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Booking counters must be changed inside a database transaction.");
    }
}
