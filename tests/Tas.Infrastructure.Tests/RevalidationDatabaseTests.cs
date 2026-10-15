using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Tas.Application.Auth;
using Tas.Application.Booking;
using Tas.Application.Tos;
using Tas.Domain;
using Tas.Domain.Booking;
using Tas.Domain.Parties;
using Tas.Infrastructure.Auth;
using Tas.Infrastructure.Booking;
using Tas.Infrastructure.Capacity;
using Tas.Infrastructure.Persistence;
using Tas.Infrastructure.Tos;

namespace Tas.Infrastructure.Tests;

/// <summary>
/// Re-checking booked appointments against the TOS. Bookings are made on Monday 2026-10-19 12:00 Riyadh
/// for Tuesday; the fake TOS data is changed afterwards to simulate holds appearing and clearing.
/// </summary>
public sealed class RevalidationDatabaseTests : IAsyncLifetime
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("TAS_TEST_DB");
    private static readonly TimeProvider Monday = Clock(2026, 10, 19, 12);

    private readonly FakeTosOptions _tos = new(); // per test: changed during the test
    private DemoSeedResult _demo = null!;

    private static TimeProvider Clock(int y, int m, int d, int riyadhHour, int minute = 0) =>
        new FixedClock(new DateTimeOffset(y, m, d, riyadhHour, minute, 0, TimeSpan.FromHours(3)));

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
    }

    private sealed class StaticOptions<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class UnreachableTos : ITosAdapter
    {
        public string Source => "N4";
        public Task<IReadOnlyList<TosCheck>> ValidateAsync(int terminalId, IReadOnlyList<TosItemQuery> items, CancellationToken ct = default) =>
            throw new HttpRequestException("TOS unreachable");

        public Task<IReadOnlyList<TosBlUnit>?> FindBlContainersAsync(int terminalId, string blNo, CancellationToken ct = default) =>
            throw new HttpRequestException("TOS unreachable");

        public Task<IReadOnlyList<TosYardPosition>> YardPositionsAsync(int terminalId, IReadOnlyList<TosItemQuery> items, CancellationToken ct = default) =>
            throw new HttpRequestException("TOS unreachable");
    }

    private sealed class Services(TimeProvider clock, ITosAdapter tos) : IAsyncDisposable
    {
        public TasDbContext Db { get; } = new(new DbContextOptionsBuilder<TasDbContext>().UseNpgsql(ConnectionString).Options);
        public AppointmentQueryService Queries => new(Db, clock);
        public BookingService Booking => new(Db, new BookingCounterStore(Db), tos, Queries, clock, NullLogger<BookingService>.Instance);
        public RevalidationService Revalidation => new(Db, tos, Queries, clock, new ConfigurationBuilder().Build(), NullLogger<RevalidationService>.Instance);
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private Services At(TimeProvider clock) => new(clock, new FakeTosAdapter(new StaticOptions<FakeTosOptions>(_tos)));

    private static DateTimeOffset Riyadh(int day, int hour) => new(2026, 10, day, hour, 0, 0, TimeSpan.FromHours(3));

    public async Task InitializeAsync()
    {
        if (ConnectionString is null)
        {
            if (Environment.GetEnvironmentVariable("CI") == "true")
                throw new InvalidOperationException("TAS_TEST_DB is not set in CI.");
            return;
        }

        await using var s = At(Monday);
        await s.Db.Database.ExecuteSqlRawAsync("TRUNCATE terminal, company, app_user RESTART IDENTITY CASCADE");
        var generator = new SlotGenerationService(s.Db, Monday, NullLogger<SlotGenerationService>.Instance);
        _demo = await new DemoDataSeeder(s.Db, generator, new PasswordService(), Monday).SeedAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private CurrentUser Dispatcher => new(_demo.DispatcherUserId, UserRole.CompanyAdmin, _demo.CompanyId, null);

    /// <summary>Books one import pickup (with truck and driver) on Monday into the given Riyadh slot; returns its id and number.</summary>
    private async Task<(long Id, string No)> BookAsync(int day, int hour, string container)
    {
        await using var s = At(Monday);
        var start = Riyadh(day, hour).ToUniversalTime();
        var slotId = await s.Db.Slots.Where(x => x.TerminalId == _demo.TerminalId && x.StartAt == start).Select(x => x.SlotId).SingleAsync();
        var outcome = await s.Booking.CreateAsync(_demo.TerminalId, new BookingRequest(
            _demo.CompanyId, _demo.DispatcherUserId, slotId, _demo.TruckIds[0], _demo.DriverIds[0],
            [new BookingItemRequest("IMP_PICKUP", ContainerNo: container)]));
        Assert.Equal(BookingOutcomeKind.Created, outcome.Kind);
        var id = await s.Db.Appointments.Where(a => a.AppointmentNo == outcome.Appointment!.AppointmentNo).Select(a => a.AppointmentId).SingleAsync();
        return (id, outcome.Appointment!.AppointmentNo);
    }

    [Fact]
    public async Task A_hold_after_booking_turns_the_light_red_and_clearing_it_turns_it_back()
    {
        if (ConnectionString is null)
            return;

        var (id, no) = await BookAsync(20, 10, "MSCU1234566");

        // Customs put a hold on the container after it was booked.
        _tos.CustomsHold.Add("MSCU1234566");
        await using (var s = At(Monday))
        {
            var result = await s.Revalidation.RevalidateAsync(id, "scheduled");
            Assert.Equal(TrafficLight.Red, result!.Light);
            Assert.Contains("Customs hold on MSCU1234566", Assert.Single(result.Changes));

            var stored = await s.Db.Appointments.AsNoTracking().Include(a => a.Items).SingleAsync(a => a.AppointmentId == id);
            Assert.Equal(TrafficLight.Red, stored.TrafficLight);
            Assert.Equal(ValidationStatus.Fail, stored.Items.Single().ValidationStatus);
            Assert.Equal(AppointmentStatus.Confirmed, stored.Status); // capacity is kept; dispatcher decides

            var notifications = await s.Db.NotificationLogs.AsNoTracking().Where(n => n.AppointmentId == id).ToListAsync();
            Assert.Equal(2, notifications.Count);
            Assert.All(notifications, n => Assert.Equal(NotificationTemplates.StatusRed, n.TemplateCode));
            Assert.Contains(notifications, n => n.Channel == NotificationChannel.Sms && n.DriverId == _demo.DriverIds[0] && n.Language == "ar");
            Assert.Contains(notifications, n => n.Channel == NotificationChannel.Email && n.UserId == _demo.DispatcherUserId);

            Assert.Contains(await s.Db.AppointmentStatusHistory.AsNoTracking().Where(h => h.AppointmentId == id).Select(h => h.Reason).ToListAsync(),
                r => r!.StartsWith("Re-check (scheduled)") && r.Contains("Customs hold"));
        }

        // Nothing changed: no new history or notifications.
        await using (var s = At(Monday))
        {
            Assert.Empty((await s.Revalidation.RevalidateAsync(id, "scheduled"))!.Changes);
            Assert.Equal(2, await s.Db.NotificationLogs.CountAsync(n => n.AppointmentId == id));
        }

        // Customs release the container.
        _tos.CustomsHold.Clear();
        await using (var s = At(Monday))
        {
            var result = await s.Revalidation.RevalidateAsync(id, "scheduled");
            Assert.Equal(TrafficLight.Grey, result!.Light); // clear, but not yet time to come
            Assert.Single(result.Changes);

            var cleared = await s.Db.NotificationLogs.AsNoTracking().Where(n => n.AppointmentId == id && n.TemplateCode == NotificationTemplates.StatusCleared).CountAsync();
            Assert.Equal(2, cleared);
            Assert.Equal(TrafficLight.Grey, (await s.Queries.GetAsync(_demo.TerminalId, no))!.TrafficLight);
        }
    }

    [Fact]
    public async Task Scheduled_run_rechecks_only_upcoming_appointments_that_are_due()
    {
        if (ConnectionString is null)
            return;

        var (soon, _) = await BookAsync(20, 10, "MSCU1234566");     // Tuesday 10:00 — 22 h ahead
        var (later, _) = await BookAsync(21, 10, "MAEU4001110");    // Wednesday 10:00 — beyond 24 h
        var (cancelled, cancelledNo) = await BookAsync(20, 11, "CMAU5100012");
        await using (var s = At(Monday))
            Assert.Equal(BookingOutcomeKind.Updated, (await s.Booking.CancelAsync(_demo.TerminalId, cancelledNo, _demo.DispatcherUserId, "test")).Kind);

        // Just booked = just checked.
        await using (var s = At(Monday))
            Assert.Equal(0, await s.Revalidation.RunDueAsync());

        // Two hours later the Tuesday appointment is due; Wednesday's is too far away; cancelled ones never.
        var twoHoursLater = Clock(2026, 10, 19, 14);
        await using (var s = At(twoHoursLater))
        {
            Assert.Equal(1, await s.Revalidation.RunDueAsync());
            var validated = await s.Db.Appointments.AsNoTracking().ToDictionaryAsync(a => a.AppointmentId, a => a.LastValidatedAt);
            Assert.Equal(twoHoursLater.GetUtcNow(), validated[soon]);
            Assert.Equal(Monday.GetUtcNow(), validated[later]);
            Assert.Equal(Monday.GetUtcNow(), validated[cancelled]);
        }

        await using (var s = At(twoHoursLater))
            Assert.Equal(0, await s.Revalidation.RunDueAsync());
    }

    [Fact]
    public async Task Start_trip_is_refused_while_red_and_marks_the_truck_en_route_when_clear()
    {
        if (ConnectionString is null)
            return;

        var (id, no) = await BookAsync(20, 10, "MSCU1234566");

        await using (var s = At(Monday))
        {
            var early = await s.Revalidation.StartTripAsync(_demo.TerminalId, no, Dispatcher);
            Assert.Equal(BookingOutcomeKind.Conflict, early.Kind);
            Assert.Contains("Too early", early.Errors[0]);
        }

        var tuesdayMorning = Clock(2026, 10, 20, 8);
        _tos.CustomsHold.Add("MSCU1234566");
        await using (var s = At(tuesdayMorning))
        {
            var refused = await s.Revalidation.StartTripAsync(_demo.TerminalId, no, Dispatcher);
            Assert.Equal(BookingOutcomeKind.Rejected, refused.Kind);
            Assert.Contains("Don't set off: Customs hold on MSCU1234566", refused.Errors[0]);
            Assert.Equal(AppointmentStatus.Confirmed, await s.Db.Appointments.Where(a => a.AppointmentId == id).Select(a => a.Status).SingleAsync());
        }

        _tos.CustomsHold.Clear();
        await using (var s = At(tuesdayMorning))
        {
            var started = await s.Revalidation.StartTripAsync(_demo.TerminalId, no, Dispatcher);
            Assert.Equal(BookingOutcomeKind.Updated, started.Kind);
            Assert.Equal(AppointmentStatus.EnRoute, started.Appointment!.Status);

            var again = await s.Revalidation.StartTripAsync(_demo.TerminalId, no, Dispatcher);
            Assert.Equal(AppointmentStatus.EnRoute, again.Appointment!.Status);
            Assert.Equal(1, await s.Db.AppointmentStatusHistory.CountAsync(h => h.AppointmentId == id && h.Reason == "Trip started"));
        }

        // Other companies can't start someone else's trip.
        await using (var s = At(tuesdayMorning))
        {
            var other = new CurrentUser(_demo.OtherDispatcherUserId, UserRole.CompanyAdmin, _demo.OtherCompanyId, null);
            Assert.Equal(BookingOutcomeKind.NotFound, (await s.Revalidation.StartTripAsync(_demo.TerminalId, no, other)).Kind);
        }
    }

    [Fact]
    public async Task An_unreachable_tos_leaves_appointments_due_for_the_next_run()
    {
        if (ConnectionString is null)
            return;

        var (id, _) = await BookAsync(20, 10, "MSCU1234566");

        await using var s = new Services(Clock(2026, 10, 19, 14), new UnreachableTos());
        Assert.Equal(0, await s.Revalidation.RunDueAsync());
        Assert.Equal(Monday.GetUtcNow(), await s.Db.Appointments.Where(a => a.AppointmentId == id).Select(a => a.LastValidatedAt).SingleAsync());
    }
}
