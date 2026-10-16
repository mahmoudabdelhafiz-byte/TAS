using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Yardsyncs.Application.Auth;
using Yardsyncs.Application.Booking;
using Yardsyncs.Application.Capacity;
using Yardsyncs.Application.Gate;
using Yardsyncs.Domain.Booking;
using Yardsyncs.Domain.Gate;
using Yardsyncs.Domain.Parties;
using Yardsyncs.Infrastructure.Auth;
using Yardsyncs.Infrastructure.Booking;
using Yardsyncs.Infrastructure.Capacity;
using Yardsyncs.Infrastructure.Gate;
using Yardsyncs.Infrastructure.Persistence;
using Yardsyncs.Infrastructure.Tos;

namespace Yardsyncs.Infrastructure.Tests;

/// <summary>
/// Gate flows on the demo terminal (lanes IN-1, IN-2, OUT-1). Bookings are made Monday for Tuesday 10:00–11:00
/// Riyadh with truck 1234 ABJ; the gate is then worked at different times on Tuesday.
/// </summary>
public sealed class GateDatabaseTests : IAsyncLifetime
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("TAS_TEST_DB");
    private readonly FakeTosOptions _tos = new();
    private DemoSeedResult _demo = null!;

    private static TimeProvider Clock(int day, int hour, int minute = 0) =>
        new FixedClock(new DateTimeOffset(2026, 10, day, hour, minute, 0, TimeSpan.FromHours(3)));

    private static readonly TimeProvider Monday = Clock(19, 12);

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

    private sealed class Services : IAsyncDisposable
    {
        public Services(TimeProvider clock, FakeTosOptions tosData)
        {
            Db = new YardsyncsDbContext(new DbContextOptionsBuilder<YardsyncsDbContext>().UseNpgsql(ConnectionString).Options);
            var tos = new FakeTosAdapter(new StaticOptions<FakeTosOptions>(tosData));
            Queries = new AppointmentQueryService(Db, clock);
            Booking = new BookingService(Db, new BookingCounterStore(Db), tos, Queries, clock, NullLogger<BookingService>.Instance);
            var revalidation = new RevalidationService(Db, tos, Queries, clock, new ConfigurationBuilder().Build(), NullLogger<RevalidationService>.Instance);
            Gate = new GateService(Db, revalidation, Booking, Queries, clock, NullLogger<GateService>.Instance);
        }

        public YardsyncsDbContext Db { get; }
        public AppointmentQueryService Queries { get; }
        public BookingService Booking { get; }
        public GateService Gate { get; }
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private Services At(TimeProvider clock) => new(clock, _tos);

    private CurrentUser Clerk => new(_demo.PlannerUserId, UserRole.Planner, null, _demo.TerminalId);

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

    /// <summary>Books an import pickup for Tuesday 10:00 on Monday with truck 1234 ABJ and its driver.</summary>
    private async Task<AppointmentDto> BookAsync(string container = "MSCU1234566")
    {
        await using var s = At(Monday);
        var start = new DateTimeOffset(2026, 10, 20, 10, 0, 0, TimeSpan.FromHours(3)).ToUniversalTime();
        var slotId = await s.Db.Slots.Where(x => x.TerminalId == _demo.TerminalId && x.StartAt == start).Select(x => x.SlotId).SingleAsync();
        var outcome = await s.Booking.CreateAsync(_demo.TerminalId, new BookingRequest(
            _demo.CompanyId, _demo.DispatcherUserId, slotId, _demo.TruckIds[0], _demo.DriverIds[0],
            [new BookingItemRequest("IMP_PICKUP", ContainerNo: container)]));
        Assert.Equal(BookingOutcomeKind.Created, outcome.Kind);
        return outcome.Appointment!;
    }

    private async Task<ArrivalResult> ArriveAsync(TimeProvider clock, ArrivalInput input)
    {
        await using var s = At(clock);
        var result = await s.Gate.ArriveAsync(Clerk, _demo.TerminalId, input);
        Assert.Equal(AdminStatus.Ok, result.Status);
        return result.Value!;
    }

    private async Task<AppointmentDto> GetAsync(string number)
    {
        await using var s = At(Monday);
        return (await s.Queries.GetAsync(_demo.TerminalId, number))!;
    }

    [Fact]
    public async Task A_normal_visit_from_plate_read_to_gate_out_with_turn_time()
    {
        if (ConnectionString is null)
            return;

        var booked = await BookAsync();

        var arrival = await ArriveAsync(Clock(20, 10, 5), new ArrivalInput("in-1", PlateRead: "أ ب ح ١٢٣٤"));
        Assert.Equal(GateDecisionKind.Admit, arrival.Decision);
        Assert.Equal(MatchMethod.Plate, arrival.MatchedBy);
        Assert.Equal(booked.AppointmentNo, arrival.AppointmentNo);
        Assert.Equal(AppointmentStatus.Arrived, arrival.Appointment!.Status);

        await using (var s = At(Clock(20, 10, 7)))
            Assert.Equal(AppointmentStatus.GateIn, (await s.Gate.GateInAsync(Clerk, _demo.TerminalId, new GateInInput(booked.AppointmentNo, "IN-1"))).Value!.Status);

        await using (var s = At(Clock(20, 10, 30)))
        {
            var board = await s.Gate.BoardAsync(_demo.TerminalId);
            Assert.Empty(board.AtGate);
            Assert.Equal(25, Assert.Single(board.InTerminal).MinutesSinceArrival);
        }

        await using (var s = At(Clock(20, 10, 50)))
        {
            var exit = await s.Gate.GateOutAsync(Clerk, _demo.TerminalId, new GateOutInput("OUT-1", PlateRead: "1234 ABJ"));
            Assert.Equal(AdminStatus.Ok, exit.Status);
            Assert.Equal(45, exit.Value!.TurnMinutes);
            Assert.Equal(43, exit.Value.InTerminalMinutes);

            var board = await s.Gate.BoardAsync(_demo.TerminalId);
            Assert.Equal(1, board.CompletedToday);
            Assert.Equal(45, board.AverageTurnMinutesToday);
            Assert.Empty(board.InTerminal);

            var events = await s.Db.GateEvents.AsNoTracking().OrderBy(e => e.GateEventId).Select(e => e.EventType).ToListAsync();
            Assert.Equal(new[] { GateEventType.OcrRead, GateEventType.Matched, GateEventType.GateIn, GateEventType.GateOut }, events.ToArray());
        }

        Assert.Equal(AppointmentStatus.Completed, (await GetAsync(booked.AppointmentNo)).Status);
    }

    [Fact]
    public async Task An_early_truck_is_referred_and_the_trouble_desk_admits_it()
    {
        if (ConnectionString is null)
            return;

        var booked = await BookAsync();
        var arrival = await ArriveAsync(Clock(20, 8, 30), new ArrivalInput("IN-2", PlateRead: "1234 ABJ"));

        Assert.Equal(GateDecisionKind.Refer, arrival.Decision);
        Assert.Equal(TroubleReason.Early, arrival.Reason);
        Assert.Contains("09:30", arrival.Message);
        Assert.Equal(AppointmentStatus.Confirmed, (await GetAsync(booked.AppointmentNo)).Status);

        await using var s = At(Clock(20, 8, 35));
        var open = await s.Gate.ListTroublesAsync(_demo.TerminalId, openOnly: true);
        var trouble = Assert.Single(open);
        Assert.Equal("IN-2", trouble.Lane);
        Assert.Equal(booked.AppointmentNo, trouble.AppointmentNo);

        Assert.Equal(AdminStatus.Invalid, (await s.Gate.ResolveAsync(Clerk, _demo.TerminalId, trouble.TroubleId, new ResolveTroubleInput(TroubleAction.Admit, null))).Status);
        var resolved = await s.Gate.ResolveAsync(Clerk, _demo.TerminalId, trouble.TroubleId, new ResolveTroubleInput(TroubleAction.Admit, "Yard is quiet, let him in"));
        Assert.Equal(AdminStatus.Ok, resolved.Status);
        Assert.Equal(TroubleResolution.Admitted, resolved.Value!.Resolution);

        var admitted = await s.Db.Appointments.AsNoTracking().SingleAsync(a => a.AppointmentNo == booked.AppointmentNo);
        Assert.Equal(AppointmentStatus.GateIn, admitted.Status);
        Assert.Equal("CLERK", admitted.CheckInMethod);
        Assert.NotNull(admitted.ArrivedAt);

        Assert.Equal(AdminStatus.Conflict, (await s.Gate.ResolveAsync(Clerk, _demo.TerminalId, trouble.TroubleId, new ResolveTroubleInput(TroubleAction.Dismiss, null))).Status);
        Assert.Empty(await s.Gate.ListTroublesAsync(_demo.TerminalId, openOnly: true));
    }

    [Fact]
    public async Task A_hold_found_at_the_gate_is_referred_and_rejecting_releases_capacity()
    {
        if (ConnectionString is null)
            return;

        var booked = await BookAsync();
        _tos.CustomsHold.Add("MSCU1234566");

        var arrival = await ArriveAsync(Clock(20, 10, 5), new ArrivalInput("IN-1", PlateRead: "1234 ABJ", ContainerReads: ["TGHU3000017"]));
        Assert.Equal(TroubleReason.NotCleared, arrival.Reason);
        Assert.Contains("Customs hold on MSCU1234566", arrival.Message);
        Assert.Contains(arrival.Warnings, w => w.Contains("TGHU3000017"));

        await using var s = At(Clock(20, 10, 10));
        var rejected = await s.Gate.ResolveAsync(Clerk, _demo.TerminalId, arrival.TroubleId!.Value, new ResolveTroubleInput(TroubleAction.Reject, "Customs hold — sent back"));
        Assert.Equal(TroubleResolution.Rejected, rejected.Value!.Resolution);

        var appointment = await s.Db.Appointments.AsNoTracking().SingleAsync(a => a.AppointmentNo == booked.AppointmentNo);
        Assert.Equal(AppointmentStatus.RejectedAtGate, appointment.Status);
        Assert.Equal(0, await s.Db.Slots.Where(x => x.SlotId == appointment.SlotId).Select(x => x.TotalBooked).SingleAsync());
    }

    [Fact]
    public async Task A_truck_without_an_appointment_is_booked_as_a_walk_in()
    {
        if (ConnectionString is null)
            return;

        var arrival = await ArriveAsync(Clock(20, 10, 5), new ArrivalInput("IN-1", PlateRead: "5678 HDR"));
        Assert.Equal(TroubleReason.NoAppointment, arrival.Reason);
        Assert.Null(arrival.AppointmentNo);

        await using var s = At(Clock(20, 10, 8));
        Assert.Equal(AdminStatus.Invalid,
            (await s.Gate.ResolveAsync(Clerk, _demo.TerminalId, arrival.TroubleId!.Value, new ResolveTroubleInput(TroubleAction.Admit, "no booking"))).Status);

        var walkIn = await s.Gate.ResolveAsync(Clerk, _demo.TerminalId, arrival.TroubleId.Value, new ResolveTroubleInput(
            TroubleAction.WalkIn, "Walk-in", WalkIn: new WalkInInput(_demo.CompanyId, [new BookingItemRequest("IMP_PICKUP", ContainerNo: "MAEU4001110")])));
        Assert.Equal(AdminStatus.Ok, walkIn.Status);
        Assert.Equal(TroubleResolution.WalkIn, walkIn.Value!.Resolution);
        Assert.NotNull(walkIn.Value.AppointmentNo);

        var appointment = await s.Queries.GetAsync(_demo.TerminalId, walkIn.Value.AppointmentNo!);
        Assert.Equal(AppointmentStatus.GateIn, appointment!.Status);
        Assert.Equal("5678 HDR", appointment.TruckPlate);
    }

    [Fact]
    public async Task Late_trucks_are_referred_and_a_second_read_of_a_truck_inside_is_flagged()
    {
        if (ConnectionString is null)
            return;

        var booked = await BookAsync();
        var late = await ArriveAsync(Clock(20, 11, 45), new ArrivalInput("IN-1", PlateRead: "1234 ABJ"));
        Assert.Equal(TroubleReason.Late, late.Reason);
        Assert.Contains("11:30", late.Message);

        await using (var s = At(Clock(20, 11, 50)))
            Assert.Equal(AdminStatus.Ok, (await s.Gate.ResolveAsync(Clerk, _demo.TerminalId, late.TroubleId!.Value,
                new ResolveTroubleInput(TroubleAction.Admit, "Supervisor approved late entry"))).Status);

        var again = await ArriveAsync(Clock(20, 11, 55), new ArrivalInput("IN-2", PlateRead: "1234 ABJ"));
        Assert.Equal(TroubleReason.AlreadyIn, again.Reason);
        Assert.Equal(booked.AppointmentNo, again.AppointmentNo);
    }

    [Fact]
    public async Task Qr_code_check_in_and_lane_validation()
    {
        if (ConnectionString is null)
            return;

        var booked = await BookAsync();

        await using (var s = At(Clock(20, 10)))
        {
            Assert.Equal(AdminStatus.Invalid, (await s.Gate.ArriveAsync(Clerk, _demo.TerminalId, new ArrivalInput("IN-9", QrToken: booked.QrToken))).Status);
            Assert.Equal(AdminStatus.Invalid, (await s.Gate.ArriveAsync(Clerk, _demo.TerminalId, new ArrivalInput("OUT-1", QrToken: booked.QrToken))).Status);
        }

        var arrival = await ArriveAsync(Clock(20, 10), new ArrivalInput("IN-1", QrToken: booked.QrToken.ToLowerInvariant()));
        Assert.Equal(GateDecisionKind.Admit, arrival.Decision);
        Assert.Equal(MatchMethod.QrCode, arrival.MatchedBy);

        await using var check = At(Monday);
        Assert.Equal("APP_MANUAL", await check.Db.Appointments.Where(a => a.AppointmentNo == booked.AppointmentNo).Select(a => a.CheckInMethod).SingleAsync());
    }
}
