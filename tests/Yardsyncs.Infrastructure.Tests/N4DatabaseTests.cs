using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Yardsyncs.Application.Auth;
using Yardsyncs.Application.Booking;
using Yardsyncs.Application.Gate;
using Yardsyncs.Application.Tos;
using Yardsyncs.Domain.Booking;
using Yardsyncs.Domain.Gate;
using Yardsyncs.Domain.Parties;
using Yardsyncs.Infrastructure.Auth;
using Yardsyncs.Infrastructure.Booking;
using Yardsyncs.Infrastructure.Capacity;
using Yardsyncs.Infrastructure.Gate;
using Yardsyncs.Infrastructure.Persistence;
using Yardsyncs.Infrastructure.Tos.N4;

namespace Yardsyncs.Infrastructure.Tests;

/// <summary>
/// Booking, re-checks and the gate with the N4 adapter, against PostgreSQL. A stub N4 answers with JSON as the
/// TasLookup code extension would, or is "down". Bookings are made Monday 12:00 Riyadh for Tuesday 10:00.
/// </summary>
public sealed class N4DatabaseTests : IAsyncLifetime
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("TAS_TEST_DB");
    private static readonly TimeProvider Monday = Clock(19, 12);

    private readonly StubN4 _n4 = new();
    private DemoSeedResult _demo = null!;

    private static TimeProvider Clock(int day, int hour, int minute = 0) =>
        new FixedClock(new DateTimeOffset(2026, 10, day, hour, minute, 0, TimeSpan.FromHours(3)));

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

    /// <summary>Answers with the configured JSON, or throws as an unreachable N4 would.</summary>
    private sealed class StubN4 : IN4Client
    {
        public string Payload { get; set; } = """{"units":[],"orders":[],"bls":[]}""";
        public bool Down { get; set; }
        public List<string> Documents { get; } = [];

        public Task<string> InvokeAsync(N4TerminalOptions terminal, string xmlDoc, CancellationToken ct)
        {
            Documents.Add(xmlDoc);
            return Down ? throw new TosUnavailableException("N4 did not answer within 15 s.") : Task.FromResult(Payload);
        }
    }

    private static readonly N4Options Options = new()
    {
        Terminals = new() { ["demo"] = new N4TerminalOptions { Url = "http://n4.test", FacilityId = "JED" } },
    };

    private sealed class Services : IAsyncDisposable
    {
        public Services(TimeProvider clock, StubN4 n4, TosUnavailablePolicy policy)
        {
            Db = new YardsyncsDbContext(new DbContextOptionsBuilder<YardsyncsDbContext>().UseNpgsql(ConnectionString).Options);
            var tos = new N4TosAdapter(Db, n4, new StaticOptions<N4Options>(Options), NullLogger<N4TosAdapter>.Instance);
            Queries = new AppointmentQueryService(Db, clock);
            Booking = new BookingService(Db, new BookingCounterStore(Db), tos, Queries, clock, NullLogger<BookingService>.Instance, new TosSettings(policy));
            Revalidation = new RevalidationService(Db, tos, Queries, clock, new ConfigurationBuilder().Build(), NullLogger<RevalidationService>.Instance);
            Gate = new GateService(Db, Revalidation, Booking, Queries, clock, NullLogger<GateService>.Instance);
        }

        public YardsyncsDbContext Db { get; }
        public AppointmentQueryService Queries { get; }
        public BookingService Booking { get; }
        public RevalidationService Revalidation { get; }
        public GateService Gate { get; }
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private Services At(TimeProvider clock, TosUnavailablePolicy policy = TosUnavailablePolicy.Warn) => new(clock, _n4, policy);

    private static string ImportUnit(params string[] holds) =>
        $$"""{"units":[{"containerNo":"MSCU1234566","found":true,"gkey":"5001","category":"IMPRT","freightKind":"FCL","transitState":"S40_YARD","holds":[{{string.Join(",", holds.Select(h => $"\"{h}\""))}}]}],"orders":[],"bls":[]}""";

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

    private async Task<BookingOutcome> BookAsync(TosUnavailablePolicy policy = TosUnavailablePolicy.Warn)
    {
        await using var s = At(Monday, policy);
        var start = new DateTimeOffset(2026, 10, 20, 10, 0, 0, TimeSpan.FromHours(3)).ToUniversalTime();
        var slotId = await s.Db.Slots.Where(x => x.TerminalId == _demo.TerminalId && x.StartAt == start).Select(x => x.SlotId).SingleAsync();
        return await s.Booking.CreateAsync(_demo.TerminalId, new BookingRequest(
            _demo.CompanyId, _demo.DispatcherUserId, slotId, _demo.TruckIds[0], _demo.DriverIds[0],
            [new BookingItemRequest("IMP_PICKUP", ContainerNo: "MSCU1234566")]));
    }

    private async Task<DateTimeOffset?> LastValidatedAsync(string appointmentNo)
    {
        await using var s = At(Monday);
        return await s.Db.Appointments.Where(a => a.AppointmentNo == appointmentNo).Select(a => a.LastValidatedAt).SingleAsync();
    }

    [Fact]
    public async Task Bookings_are_checked_against_n4()
    {
        if (ConnectionString is null)
            return;

        _n4.Payload = ImportUnit("CUSTOMS HOLD");
        var held = await BookAsync();
        Assert.Equal(BookingOutcomeKind.Rejected, held.Kind);
        Assert.Contains("Customs hold on MSCU1234566: CUSTOMS HOLD", held.Errors[0]);
        Assert.Contains("MSCU1234566", Assert.Single(_n4.Documents));

        _n4.Payload = ImportUnit();
        var booked = await BookAsync();
        Assert.Equal(BookingOutcomeKind.Created, booked.Kind);
        var checks = booked.Appointment!.Items.Single().Checks;
        Assert.Contains(checks, c => c.Type == CheckType.Availability && c.Source == "N4" && c.Result == CheckResult.Pass);
        Assert.Contains(checks, c => c.Type == CheckType.ContainerNumber && c.Source == "INTERNAL");

        await using var s = At(Monday);
        Assert.Equal("5001", await s.Db.AppointmentItems.Select(i => i.TosRef).SingleAsync());
    }

    [Fact]
    public async Task N4_down_at_booking_warns_and_the_scheduled_recheck_catches_up()
    {
        if (ConnectionString is null)
            return;

        _n4.Down = true;
        var booked = await BookAsync();
        Assert.Equal(BookingOutcomeKind.Created, booked.Kind);
        Assert.Equal(TrafficLight.Amber, booked.Appointment!.TrafficLight);
        Assert.Contains("could not be reached", booked.Appointment.TrafficReason);
        Assert.Null(await LastValidatedAsync(booked.Appointment.AppointmentNo)); // due for the next re-check

        // Still down: nothing re-checked, still due.
        await using (var s = At(Clock(19, 12, 15)))
            Assert.Equal(0, await s.Revalidation.RunDueAsync());
        Assert.Null(await LastValidatedAsync(booked.Appointment.AppointmentNo));

        // N4 is back — and has a hold on the container meanwhile.
        _n4.Down = false;
        _n4.Payload = ImportUnit("CUSTOMS HOLD");
        await using (var s = At(Clock(19, 12, 30)))
        {
            Assert.Equal(1, await s.Revalidation.RunDueAsync());
            var appointment = await s.Queries.GetAsync(_demo.TerminalId, booked.Appointment.AppointmentNo);
            Assert.Equal(TrafficLight.Red, appointment!.TrafficLight);
            Assert.DoesNotContain(appointment.Items.Single().Checks, c => c.Type == CheckType.TosUnavailable);
        }
        Assert.NotNull(await LastValidatedAsync(booked.Appointment.AppointmentNo));
    }

    [Fact]
    public async Task Reject_policy_refuses_bookings_while_n4_is_down()
    {
        if (ConnectionString is null)
            return;

        _n4.Down = true;
        var outcome = await BookAsync(TosUnavailablePolicy.Reject);

        Assert.Equal(BookingOutcomeKind.Rejected, outcome.Kind);
        Assert.Contains("can't be reached", outcome.Errors[0]);
        await using var s = At(Monday);
        Assert.Equal(0, await s.Db.Appointments.CountAsync());
    }

    [Fact]
    public async Task With_n4_down_trucks_are_not_started_or_admitted_unchecked()
    {
        if (ConnectionString is null)
            return;

        _n4.Payload = ImportUnit();
        var booked = (await BookAsync()).Appointment!;
        _n4.Down = true;

        var dispatcher = new CurrentUser(_demo.DispatcherUserId, UserRole.CompanyAdmin, _demo.CompanyId, null);
        await using (var s = At(Clock(20, 8)))
        {
            var start = await s.Revalidation.StartTripAsync(_demo.TerminalId, booked.AppointmentNo, dispatcher);
            Assert.Equal(BookingOutcomeKind.Conflict, start.Kind);
            Assert.Contains("can't be reached", start.Errors[0]);

            Assert.Equal(BookingOutcomeKind.Conflict,
                (await s.Revalidation.RevalidateByNumberAsync(_demo.TerminalId, booked.AppointmentNo, dispatcher)).Kind);
        }

        var clerk = new CurrentUser(_demo.PlannerUserId, UserRole.Planner, null, _demo.TerminalId);
        await using (var s = At(Clock(20, 10, 5)))
        {
            var arrival = (await s.Gate.ArriveAsync(clerk, _demo.TerminalId, new ArrivalInput("IN-1", PlateRead: "1234 ABJ"))).Value!;
            Assert.Equal(GateDecisionKind.Refer, arrival.Decision);
            Assert.Equal(TroubleReason.NotCleared, arrival.Reason);
            Assert.Contains("could not be reached", arrival.Message);
            Assert.Equal(booked.AppointmentNo, arrival.AppointmentNo);
        }
    }
}
