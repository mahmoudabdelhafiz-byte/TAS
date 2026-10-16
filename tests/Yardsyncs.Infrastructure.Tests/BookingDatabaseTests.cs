using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Yardsyncs.Application.Booking;
using Yardsyncs.Domain.Booking;
using Yardsyncs.Infrastructure.Auth;
using Yardsyncs.Infrastructure.Booking;
using Yardsyncs.Infrastructure.Capacity;
using Yardsyncs.Infrastructure.Persistence;
using Yardsyncs.Infrastructure.Tos;

namespace Yardsyncs.Infrastructure.Tests;

/// <summary>
/// End-to-end booking flow on the demo terminal against PostgreSQL. "Now" is Monday 2026-10-19 12:00 Riyadh;
/// bookings go into Tuesday 2026-10-20 slots. Requires TAS_TEST_DB (see CapacityDatabaseTests).
/// </summary>
public sealed class BookingDatabaseTests : IAsyncLifetime
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("TAS_TEST_DB");
    private static readonly TimeProvider Monday = new FixedClock(new DateTimeOffset(2026, 10, 19, 9, 0, 0, TimeSpan.Zero));

    private static readonly FakeTosOptions TosData = new()
    {
        CustomsHold = ["MSCU7654329"],
        PaymentWarning = ["CMAU5100028"],
        CargoNotReleased = ["BL-PIPES-002"],
    };

    private DemoSeedResult _demo = null!;

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class StaticOptions<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class Services(TimeProvider clock) : IAsyncDisposable
    {
        public YardsyncsDbContext Db { get; } = NewContext();

        public AppointmentQueryService Queries => new(Db, clock);

        public BookingService Booking => new(
            Db, new BookingCounterStore(Db), new FakeTosAdapter(new StaticOptions<FakeTosOptions>(TosData)),
            Queries, clock, NullLogger<BookingService>.Instance);

        public NoShowService NoShows => new(Db, Booking, clock, NullLogger<NoShowService>.Instance);

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private static YardsyncsDbContext NewContext() =>
        new(new DbContextOptionsBuilder<YardsyncsDbContext>().UseNpgsql(ConnectionString).Options);

    private static DateTimeOffset Tuesday(int hour) => new(2026, 10, 20, hour, 0, 0, TimeSpan.FromHours(3));

    public async Task InitializeAsync()
    {
        if (ConnectionString is null)
        {
            if (Environment.GetEnvironmentVariable("CI") == "true")
                throw new InvalidOperationException("TAS_TEST_DB is not set in CI.");
            return;
        }

        await using (var db = NewContext())
            await db.Database.ExecuteSqlRawAsync("TRUNCATE terminal, company RESTART IDENTITY CASCADE");

        await using (var db = NewContext())
        {
            var generator = new SlotGenerationService(db, Monday, NullLogger<SlotGenerationService>.Instance);
            _demo = await new DemoDataSeeder(db, generator, new PasswordService(), Monday).SeedAsync();
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<long> SlotIdAsync(int hour)
    {
        await using var db = NewContext();
        var start = Tuesday(hour).ToUniversalTime();
        return await db.Slots.Where(s => s.TerminalId == _demo.TerminalId && s.StartAt == start).Select(s => s.SlotId).SingleAsync();
    }

    private async Task<(int Total, Dictionary<string, int> ByCategory)> CountersAsync(long slotId)
    {
        await using var db = NewContext();
        var total = await db.Slots.Where(s => s.SlotId == slotId).Select(s => s.TotalBooked).SingleAsync();
        var byCategory = await db.SlotCategoryCapacities
            .Where(c => c.SlotId == slotId)
            .Join(db.TransactionCategories, c => c.CategoryId, t => t.CategoryId, (c, t) => new { t.Code, c.Booked })
            .ToDictionaryAsync(x => x.Code, x => x.Booked);
        return (total, byCategory);
    }

    private BookingRequest Request(long slotId, params BookingItemRequest[] items) =>
        new(_demo.CompanyId, _demo.DispatcherUserId, slotId, _demo.TruckIds[0], _demo.DriverIds[0], items);

    private async Task<BookingOutcome> BookAsync(long slotId, params BookingItemRequest[] items)
    {
        await using var s = new Services(Monday);
        return await s.Booking.CreateAsync(_demo.TerminalId, Request(slotId, items));
    }

    [Fact]
    public async Task Demo_seed_is_idempotent_and_generates_slots()
    {
        if (ConnectionString is null)
            return;

        await using var db = NewContext();
        var generator = new SlotGenerationService(db, Monday, NullLogger<SlotGenerationService>.Instance);
        var again = await new DemoDataSeeder(db, generator, new PasswordService(), Monday).SeedAsync();

        Assert.False(again.Created);
        Assert.Equal(_demo.TerminalId, again.TerminalId);
        Assert.True(await db.Slots.CountAsync(s => s.TerminalId == _demo.TerminalId) > 100);
    }

    [Fact]
    public async Task Dual_booking_is_confirmed_and_consumes_one_truck_and_two_quotas()
    {
        if (ConnectionString is null)
            return;

        var slotId = await SlotIdAsync(10);
        var outcome = await BookAsync(slotId,
            new BookingItemRequest("EXP_DROP", ContainerNo: "HLXU8700018", BookingRef: "BKG1001"),
            new BookingItemRequest("IMP_PICKUP", ContainerNo: "mscu 123456-6"));

        Assert.Equal(BookingOutcomeKind.Created, outcome.Kind);
        var a = outcome.Appointment!;
        Assert.StartsWith("DEMO-261020-", a.AppointmentNo);
        Assert.Equal(AppointmentStatus.Confirmed, a.Status);
        Assert.True(a.IsDual);
        Assert.Equal(TrafficLight.Grey, a.TrafficLight); // not time to come yet
        Assert.Equal("1234 ABJ", a.TruckPlate);
        Assert.All(a.Items, i => Assert.Equal(ValidationStatus.Pass, i.ValidationStatus));
        Assert.Equal("MSCU1234566", a.Items[1].ContainerNo);
        Assert.Contains(a.Items[1].Checks, c => c.Type == CheckType.CustomsRelease && c.Result == CheckResult.Pass && c.Source == "FAKE_TOS");
        Assert.Contains(a.Items[1].Checks, c => c.Type == CheckType.ContainerNumber && c.Source == "INTERNAL");

        var (total, byCategory) = await CountersAsync(slotId);
        Assert.Equal(1, total);
        Assert.Equal(1, byCategory["EXP_DROP"]);
        Assert.Equal(1, byCategory["IMP_PICKUP"]);

        await using var s = new Services(Monday);
        var reloaded = await s.Queries.GetAsync(_demo.TerminalId, a.AppointmentNo.ToLowerInvariant());
        Assert.NotNull(reloaded);
        Assert.Equal(2, reloaded.Items.Count);
    }

    [Fact]
    public async Task Customs_hold_rejects_the_booking_without_using_capacity()
    {
        if (ConnectionString is null)
            return;

        var slotId = await SlotIdAsync(10);
        var outcome = await BookAsync(slotId, new BookingItemRequest("IMP_PICKUP", ContainerNo: "MSCU7654329"));

        Assert.Equal(BookingOutcomeKind.Rejected, outcome.Kind);
        Assert.Contains(outcome.Errors, e => e.Contains("Customs hold on MSCU7654329"));
        Assert.Contains(outcome.Checks, c => c.Type == CheckType.CustomsRelease && c.Result == CheckResult.Fail);
        Assert.Equal(0, (await CountersAsync(slotId)).Total);
    }

    [Fact]
    public async Task Wrong_check_digit_is_rejected()
    {
        if (ConnectionString is null)
            return;

        var outcome = await BookAsync(await SlotIdAsync(10), new BookingItemRequest("IMP_PICKUP", ContainerNo: "MSCU1234567"));

        Assert.Equal(BookingOutcomeKind.Rejected, outcome.Kind);
        Assert.Contains(outcome.Checks, c => c.Type == CheckType.ContainerNumber && c.Result == CheckResult.Fail);
    }

    [Fact]
    public async Task A_container_cannot_have_two_live_appointments()
    {
        if (ConnectionString is null)
            return;

        Assert.Equal(BookingOutcomeKind.Created, (await BookAsync(await SlotIdAsync(10), new BookingItemRequest("IMP_PICKUP", ContainerNo: "MSCU1234566"))).Kind);
        var second = await BookAsync(await SlotIdAsync(14), new BookingItemRequest("IMP_PICKUP", ContainerNo: "MSCU1234566"));

        Assert.Equal(BookingOutcomeKind.Conflict, second.Kind);
        Assert.Contains("already has a live appointment", second.Errors[0]);
    }

    [Fact]
    public async Task Payment_warning_books_with_an_amber_light()
    {
        if (ConnectionString is null)
            return;

        var outcome = await BookAsync(await SlotIdAsync(10), new BookingItemRequest("IMP_PICKUP", ContainerNo: "CMAU5100028"));

        Assert.Equal(BookingOutcomeKind.Created, outcome.Kind);
        Assert.Equal(TrafficLight.Amber, outcome.Appointment!.TrafficLight);
        Assert.Contains("Storage charges", outcome.Appointment.TrafficReason);
    }

    [Fact]
    public async Task Category_quota_runs_out()
    {
        if (ConnectionString is null)
            return;

        // Empty pickups: weekday quota 6 (+5% overbooking, rounded down = 6). Booked by empty-release reference only.
        var slotId = await SlotIdAsync(10);
        for (var i = 1; i <= 6; i++)
            Assert.Equal(BookingOutcomeKind.Created, (await BookWithoutTruck(slotId, new BookingItemRequest("MTY_PICKUP", BookingRef: $"REL-{i}"))).Kind);

        var seventh = await BookWithoutTruck(slotId, new BookingItemRequest("MTY_PICKUP", BookingRef: "REL-7"));
        Assert.Equal(BookingOutcomeKind.NoCapacity, seventh.Kind);
        Assert.Equal(6, (await CountersAsync(slotId)).Total);
    }

    private async Task<BookingOutcome> BookWithoutTruck(long slotId, BookingItemRequest item)
    {
        await using var s = new Services(Monday);
        return await s.Booking.CreateAsync(_demo.TerminalId, new BookingRequest(_demo.CompanyId, _demo.DispatcherUserId, slotId, null, null, [item]));
    }

    [Fact]
    public async Task Cancel_gives_capacity_back()
    {
        if (ConnectionString is null)
            return;

        var slotId = await SlotIdAsync(10);
        var booked = await BookAsync(slotId, new BookingItemRequest("IMP_PICKUP", ContainerNo: "MSCU1234566"));
        Assert.Equal(1, (await CountersAsync(slotId)).Total);

        await using var s = new Services(Monday);
        var cancelled = await s.Booking.CancelAsync(_demo.TerminalId, booked.Appointment!.AppointmentNo, _demo.DispatcherUserId, "Customer changed plan");

        Assert.Equal(BookingOutcomeKind.Updated, cancelled.Kind);
        Assert.Equal(AppointmentStatus.Cancelled, cancelled.Appointment!.Status);
        var (total, byCategory) = await CountersAsync(slotId);
        Assert.Equal(0, total);
        Assert.Equal(0, byCategory["IMP_PICKUP"]);

        // Cancelling twice is refused, and the container can be booked again.
        await using var s2 = new Services(Monday);
        Assert.Equal(BookingOutcomeKind.Conflict,
            (await s2.Booking.CancelAsync(_demo.TerminalId, booked.Appointment.AppointmentNo, _demo.DispatcherUserId, null)).Kind);
        Assert.Equal(BookingOutcomeKind.Created,
            (await BookAsync(slotId, new BookingItemRequest("IMP_PICKUP", ContainerNo: "MSCU1234566"))).Kind);
    }

    [Fact]
    public async Task Reschedule_moves_capacity_to_the_new_slot()
    {
        if (ConnectionString is null)
            return;

        var ten = await SlotIdAsync(10);
        var eleven = await SlotIdAsync(11);
        var booked = await BookAsync(ten, new BookingItemRequest("IMP_PICKUP", ContainerNo: "MSCU1234566"));

        await using var s = new Services(Monday);
        var moved = await s.Booking.RescheduleAsync(_demo.TerminalId, booked.Appointment!.AppointmentNo, _demo.DispatcherUserId, eleven);

        Assert.Equal(BookingOutcomeKind.Updated, moved.Kind);
        Assert.Equal(eleven, moved.Appointment!.SlotId);
        Assert.Equal(0, (await CountersAsync(ten)).Total);
        Assert.Equal(1, (await CountersAsync(eleven)).ByCategory["IMP_PICKUP"]);

        var history = await s.Db.AppointmentStatusHistory.AsNoTracking().CountAsync();
        Assert.Equal(2, history); // booked + moved
    }

    [Fact]
    public async Task Breakbulk_trips_draw_down_cargo_and_unreleased_cargo_is_rejected()
    {
        if (ConnectionString is null)
            return;

        var slotId = await SlotIdAsync(10);
        var ok = await BookAsync(slotId, new BookingItemRequest("BB_DELIVERY", CargoRef: "bl-steel-001", PlannedTonnage: 28));
        Assert.Equal(BookingOutcomeKind.Created, ok.Kind);
        Assert.Equal("BL-STEEL-001", ok.Appointment!.Items[0].CargoRef);

        var notReleased = await BookAsync(slotId, new BookingItemRequest("BB_DELIVERY", CargoRef: "BL-PIPES-002"));
        Assert.Equal(BookingOutcomeKind.Rejected, notReleased.Kind);
        Assert.Contains(notReleased.Checks, c => c.Type == CheckType.CargoRelease && c.Result == CheckResult.Fail);

        await using var db = NewContext();
        Assert.Equal(1, await db.CargoAllocations.Where(c => c.CargoRef == "BL-STEEL-001").Select(c => c.BookedTrips).SingleAsync());
        Assert.Equal(0, await db.CargoAllocations.Where(c => c.CargoRef == "BL-PIPES-002").Select(c => c.BookedTrips).SingleAsync());
    }

    [Fact]
    public async Task Trucks_that_never_arrive_become_no_shows()
    {
        if (ConnectionString is null)
            return;

        var slotId = await SlotIdAsync(10);
        var booked = await BookAsync(slotId, new BookingItemRequest("IMP_PICKUP", ContainerNo: "MSCU1234566"));

        // Tuesday 11:31: the 10:00–11:00 slot plus 30 minutes late grace has passed.
        await using var s = new Services(new FixedClock(Tuesday(11).AddMinutes(31)));
        Assert.Equal(1, await s.NoShows.MarkNoShowsAsync());
        Assert.Equal(0, await s.NoShows.MarkNoShowsAsync()); // idempotent

        var appointment = await s.Queries.GetAsync(_demo.TerminalId, booked.Appointment!.AppointmentNo);
        Assert.Equal(AppointmentStatus.NoShow, appointment!.Status);
        Assert.Equal(0, (await CountersAsync(slotId)).Total);
        Assert.Equal(1, await s.Db.Companies.Where(c => c.CompanyId == _demo.CompanyId).Select(c => c.NoShowCount).SingleAsync());
    }

    [Fact]
    public async Task Users_cannot_book_for_other_companies()
    {
        if (ConnectionString is null)
            return;

        await using var s = new Services(Monday);
        var other = new Domain.Parties.Company { Name = "Other Co" };
        s.Db.Companies.Add(other);
        await s.Db.SaveChangesAsync();

        var outcome = await s.Booking.CreateAsync(_demo.TerminalId, new BookingRequest(
            other.CompanyId, _demo.DispatcherUserId, await SlotIdAsync(10), null, null,
            [new BookingItemRequest("IMP_PICKUP", ContainerNo: "MSCU1234566")]));

        Assert.Equal(BookingOutcomeKind.Invalid, outcome.Kind);
    }
}
