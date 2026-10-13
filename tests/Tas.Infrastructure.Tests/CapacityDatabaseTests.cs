using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Tas.Domain;
using Tas.Domain.Capacity;
using Tas.Infrastructure.Capacity;
using Tas.Infrastructure.Persistence;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Tas.Infrastructure.Tests;

/// <summary>
/// Runs against a real PostgreSQL + PostGIS database created from db/schema.sql.
/// Set TAS_TEST_DB to a connection string to run them (CI does this); otherwise they are no-ops.
/// </summary>
public sealed class CapacityDatabaseTests : IAsyncLifetime
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("TAS_TEST_DB");

    // Monday 12:00 Riyadh; the slots under test are on Tuesday 2026-10-20.
    private static readonly TimeProvider Clock = new FixedClock(new DateTimeOffset(2026, 10, 19, 9, 0, 0, TimeSpan.Zero));
    private static readonly DateOnly Tuesday = new(2026, 10, 20);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed record Seeded(int TerminalId, int ImportId, int ExportId);

    // Deliberately +03:00: the DbContext must normalize offsets to UTC before writing.
    private static DateTimeOffset Riyadh(int hour) => new(2026, 10, 20, hour, 0, 0, TimeSpan.FromHours(3));

    private static TasDbContext NewContext() =>
        new(new DbContextOptionsBuilder<TasDbContext>().UseNpgsql(ConnectionString).Options);

    private static SlotGenerationService Generator(TasDbContext db) =>
        new(db, Clock, NullLogger<SlotGenerationService>.Instance);

    public async Task InitializeAsync()
    {
        if (ConnectionString is null)
        {
            // In CI the database must be there; never let these tests pass silently.
            if (Environment.GetEnvironmentVariable("CI") == "true")
                throw new InvalidOperationException("TAS_TEST_DB is not set in CI.");
            return;
        }

        await using var db = NewContext();
        await db.Database.ExecuteSqlRawAsync("TRUNCATE terminal, company RESTART IDENTITY CASCADE");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>Tuesday 07:00–10:00 Riyadh, hourly slots, given total and import quota; export has no explicit quota.</summary>
    private static async Task<Seeded> SeedAsync(int total, int importQuota)
    {
        await using var db = NewContext();

        var terminal = new Terminal
        {
            Code = "JED1", Name = "Jeddah Test", TerminalType = TerminalType.Container, TimeZone = "Asia/Riyadh", SlotMinutes = 60,
        };
        db.Terminals.Add(terminal);
        await db.SaveChangesAsync();

        var import = new TransactionCategory
        {
            TerminalId = terminal.TerminalId, Code = "IMP_PICKUP", NameEn = "Import pickup", NameAr = "استلام وارد", Mode = CategoryMode.Container,
        };
        var export = new TransactionCategory
        {
            TerminalId = terminal.TerminalId, Code = "EXP_DROP", NameEn = "Export drop-off", NameAr = "تسليم صادر", Mode = CategoryMode.Container,
        };
        db.TransactionCategories.AddRange(import, export);
        await db.SaveChangesAsync();

        db.CapacityTemplates.Add(new CapacityTemplate
        {
            TerminalId = terminal.TerminalId,
            Name = "Default",
            IsDefault = true,
            Slots =
            [
                new CapacityTemplateSlot
                {
                    Day = DayOfWeek.Tuesday,
                    StartTime = new TimeOnly(7, 0),
                    EndTime = new TimeOnly(10, 0),
                    TotalCapacity = total,
                    Quotas = [new CapacityTemplateQuota { CategoryId = import.CategoryId, MaxUnits = importQuota }],
                },
            ],
        });
        await db.SaveChangesAsync();

        return new Seeded(terminal.TerminalId, import.CategoryId, export.CategoryId);
    }

    private static async Task<long> FirstSlotIdAsync()
    {
        await using var db = NewContext();
        return await db.Slots.OrderBy(s => s.StartAt).Select(s => s.SlotId).FirstAsync();
    }

    private static async Task<bool> TryBookAsync(long slotId, int categoryId, bool quotaReleased = false)
    {
        await using var db = NewContext();
        await using var tx = await db.Database.BeginTransactionAsync();
        var released = quotaReleased ? new HashSet<int> { categoryId } : new HashSet<int>();
        var ok = await new BookingCounterStore(db).TryConsumeAsync(
            slotId, new Dictionary<int, int> { [categoryId] = 1 }, reservedUnits: 0, released);
        if (ok)
            await tx.CommitAsync();
        else
            await tx.RollbackAsync();
        return ok;
    }

    [Fact]
    public async Task Generates_slots_and_regeneration_keeps_bookings()
    {
        if (ConnectionString is null)
            return;

        var ids = await SeedAsync(total: 4, importQuota: 2);

        await using (var db = NewContext())
        {
            var first = await Generator(db).GenerateAsync(ids.TerminalId, Tuesday, Tuesday);
            Assert.Equal(3, first.Created);
        }

        var slotId = await FirstSlotIdAsync();
        Assert.True(await TryBookAsync(slotId, ids.ImportId));
        Assert.True(await TryBookAsync(slotId, ids.ImportId));
        Assert.True(await TryBookAsync(slotId, ids.ExportId));

        // Crane maintenance cuts the 07:00 slot to 2 trucks after 3 are already booked.
        await using (var db = NewContext())
        {
            db.CapacityOverrides.Add(new CapacityOverride
            {
                TerminalId = ids.TerminalId,
                StartAt = Riyadh(7),
                EndAt = Riyadh(8),
                Action = OverrideAction.Set,
                Value = 2,
                Reason = "Crane maintenance",
                CreatedBy = 1,
            });
            await db.SaveChangesAsync();

            var second = await Generator(db).GenerateAsync(ids.TerminalId, Tuesday, Tuesday);
            Assert.Equal(0, second.Created);
            Assert.Equal(3, second.Updated);
            Assert.Equal(1, second.OverCapacity);
        }

        await using (var db = NewContext())
        {
            var slots = await db.Slots.AsNoTracking().Include(s => s.Categories).OrderBy(s => s.StartAt).ToListAsync();

            Assert.Equal(3, slots.Count);
            Assert.Equal(new DateTimeOffset(2026, 10, 20, 4, 0, 0, TimeSpan.Zero), slots[0].StartAt);

            Assert.Equal(2, slots[0].TotalCapacity);
            Assert.Equal(3, slots[0].TotalBooked);   // bookings kept
            Assert.True(slots[0].OverCapacity);      // flagged for operators

            Assert.Equal(4, slots[1].TotalCapacity);
            Assert.False(slots[1].OverCapacity);
            Assert.Equal(2, slots[1].Categories.Single(c => c.CategoryId == ids.ImportId).QuotaCapacity);
            Assert.Equal(4, slots[1].Categories.Single(c => c.CategoryId == ids.ExportId).QuotaCapacity);
        }
    }

    [Fact]
    public async Task Concurrent_bookings_never_exceed_the_quota()
    {
        if (ConnectionString is null)
            return;

        var ids = await SeedAsync(total: 4, importQuota: 2);
        await using (var db = NewContext())
            await Generator(db).GenerateAsync(ids.TerminalId, Tuesday, Tuesday);

        var slotId = await FirstSlotIdAsync();

        // Ten dispatchers race for an import quota of 2.
        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => TryBookAsync(slotId, ids.ImportId)));
        Assert.Equal(2, results.Count(ok => ok));

        // Once the import quota is released to the pool, the remaining slot total can be used.
        Assert.True(await TryBookAsync(slotId, ids.ImportId, quotaReleased: true));
        Assert.True(await TryBookAsync(slotId, ids.ImportId, quotaReleased: true));
        Assert.False(await TryBookAsync(slotId, ids.ImportId, quotaReleased: true)); // total of 4 reached

        await using (var db = NewContext())
        {
            var slot = await db.Slots.AsNoTracking().Include(s => s.Categories).SingleAsync(s => s.SlotId == slotId);
            Assert.Equal(4, slot.TotalBooked);
            Assert.Equal(4, slot.Categories.Single(c => c.CategoryId == ids.ImportId).Booked);
        }
    }

    [Fact]
    public async Task Counters_require_a_transaction()
    {
        if (ConnectionString is null)
            return;

        await using var db = NewContext();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new BookingCounterStore(db).TryConsumeAsync(1, new Dictionary<int, int>(), 0, new HashSet<int>()));
    }

    [Fact]
    public async Task Availability_reflects_quotas_bookings_and_reservations()
    {
        if (ConnectionString is null)
            return;

        var ids = await SeedAsync(total: 4, importQuota: 2);
        await using (var db = NewContext())
            await Generator(db).GenerateAsync(ids.TerminalId, Tuesday, Tuesday);

        var slotId = await FirstSlotIdAsync();
        Assert.True(await TryBookAsync(slotId, ids.ImportId));

        // A walk-in reserve of 1 truck per slot between 09:00 and 10:00.
        await using (var db = NewContext())
        {
            db.CapacityReservations.Add(new CapacityReservation
            {
                TerminalId = ids.TerminalId, StartAt = Riyadh(9), EndAt = Riyadh(10), UnitsPerSlot = 1,
            });
            await db.SaveChangesAsync();
        }

        await using (var db = NewContext())
        {
            var service = new AvailabilityQueryService(db, Clock);

            var import = await service.GetAsync(ids.TerminalId, Tuesday, "IMP_PICKUP", companyId: null);
            var export = await service.GetAsync(ids.TerminalId, Tuesday, "EXP_DROP", companyId: null);

            Assert.Equal(new[] { 1, 2, 2 }, import.Select(s => s.Available).ToArray());
            Assert.Equal(new[] { 3, 4, 3 }, export.Select(s => s.Available).ToArray());

            await Assert.ThrowsAsync<KeyNotFoundException>(() =>
                service.GetAsync(ids.TerminalId, Tuesday, "NOPE", companyId: null));
        }
    }
}
