using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Yardsyncs.Application.Auth;
using Yardsyncs.Application.Booking;
using Yardsyncs.Application.Capacity;
using Yardsyncs.Application.Notifications;
using Yardsyncs.Domain;
using Yardsyncs.Domain.Capacity;
using Yardsyncs.Domain.Parties;
using Yardsyncs.Infrastructure.Auth;
using Yardsyncs.Infrastructure.Booking;
using Yardsyncs.Infrastructure.Capacity;
using Yardsyncs.Infrastructure.Notifications;
using Yardsyncs.Infrastructure.Persistence;
using Yardsyncs.Infrastructure.Tos;

namespace Yardsyncs.Infrastructure.Tests;

/// <summary>
/// Notification outbox and delivery against PostgreSQL. Bookings are made Monday 12:00 Riyadh for Tuesday 10:00
/// with driver Abdullah (Arabic, +966500000001) by the Al Noor dispatcher.
/// </summary>
public sealed class NotificationDatabaseTests : IAsyncLifetime
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("TAS_TEST_DB");
    private static readonly TimeProvider Monday = Clock(19, 12);

    private readonly FakeTosOptions _tos = new();
    private readonly RecordingSender _sms = new(NotificationChannel.Sms);
    private readonly RecordingSender _email = new(NotificationChannel.Email);
    private readonly RecordingSender _push = new(NotificationChannel.Push);
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

    /// <summary>Records what would be sent; can be told to fail.</summary>
    private sealed class RecordingSender(NotificationChannel channel) : INotificationSender
    {
        public NotificationChannel Channel => channel;
        public string Provider => "Test";
        public List<(string To, RenderedMessage Message)> Sent { get; } = [];
        public NotificationSendException? FailWith { get; set; }

        public Task<string?> SendAsync(string recipient, RenderedMessage message, CancellationToken ct)
        {
            if (FailWith is not null)
                throw FailWith;
            Sent.Add((recipient, message));
            return Task.FromResult<string?>($"msg-{Sent.Count}");
        }
    }

    private sealed class Services : IAsyncDisposable
    {
        public Services(TimeProvider clock, FakeTosOptions tosData, NotificationSenders senders)
        {
            Db = new YardsyncsDbContext(new DbContextOptionsBuilder<YardsyncsDbContext>().UseNpgsql(ConnectionString).Options);
            var tos = new FakeTosAdapter(new StaticOptions<FakeTosOptions>(tosData));
            var config = new ConfigurationBuilder().Build();
            Queries = new AppointmentQueryService(Db, clock);
            Booking = new BookingService(Db, new BookingCounterStore(Db), tos, Queries, clock, NullLogger<BookingService>.Instance);
            Revalidation = new RevalidationService(Db, tos, Queries, clock, config, NullLogger<RevalidationService>.Instance);
            Dispatcher = new NotificationDispatcher(Db, senders, new StaticOptions<NotificationOptions>(new NotificationOptions()), clock, NullLogger<NotificationDispatcher>.Instance);
            Capacity = new CapacityAdminService(Db, new SlotGenerationService(Db, clock, NullLogger<SlotGenerationService>.Instance),
                clock, NullLogger<CapacityAdminService>.Instance, config);
        }

        public YardsyncsDbContext Db { get; }
        public AppointmentQueryService Queries { get; }
        public BookingService Booking { get; }
        public RevalidationService Revalidation { get; }
        public NotificationDispatcher Dispatcher { get; }
        public CapacityAdminService Capacity { get; }
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private Services At(TimeProvider clock) => new(clock, _tos, new NotificationSenders([_sms, _email, _push]));

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

    private async Task<(long Id, string No)> BookAsync()
    {
        await using var s = At(Monday);
        var start = new DateTimeOffset(2026, 10, 20, 10, 0, 0, TimeSpan.FromHours(3)).ToUniversalTime();
        var slotId = await s.Db.Slots.Where(x => x.TerminalId == _demo.TerminalId && x.StartAt == start).Select(x => x.SlotId).SingleAsync();
        var outcome = await s.Booking.CreateAsync(_demo.TerminalId, new BookingRequest(
            _demo.CompanyId, _demo.DispatcherUserId, slotId, _demo.TruckIds[0], _demo.DriverIds[0],
            [new BookingItemRequest("IMP_PICKUP", ContainerNo: "MSCU1234566")]));
        Assert.Equal(BookingOutcomeKind.Created, outcome.Kind);
        var id = await s.Db.Appointments.Where(a => a.AppointmentNo == outcome.Appointment!.AppointmentNo).Select(a => a.AppointmentId).SingleAsync();
        return (id, outcome.Appointment!.AppointmentNo);
    }

    /// <summary>Books, then a customs hold appears and the re-check queues STATUS_RED for driver and dispatcher.</summary>
    private async Task<(long Id, string No)> BookAndHoldAsync()
    {
        var booked = await BookAsync();
        _tos.CustomsHold.Add("MSCU1234566");
        await using var s = At(Monday);
        await s.Revalidation.RevalidateAsync(booked.Id, "scheduled");
        return booked;
    }

    private async Task<List<NotificationLog>> LogAsync()
    {
        await using var s = At(Monday);
        return await s.Db.NotificationLogs.AsNoTracking().OrderBy(n => n.NotificationId).ToListAsync();
    }

    /// <summary>Signs driver Abdullah in to the app on a phone with a push token.</summary>
    private async Task<long> AppWithPushAsync(string token)
    {
        await using var s = At(Monday);
        var device = new DriverDevice { DriverId = _demo.DriverIds[0], InstallId = "install-push-0001", Platform = "ANDROID", PushToken = token, IsActive = true, RegisteredAt = Monday.GetUtcNow() };
        s.Db.DriverDevices.Add(device);
        await s.Db.SaveChangesAsync();
        return device.DeviceId;
    }

    [Fact]
    public async Task Drivers_with_the_app_get_push_and_others_sms()
    {
        if (ConnectionString is null)
            return;

        var (id, no) = await BookAsync();
        var values = new Dictionary<string, string> { [NotificationText.Keys.AppointmentNo] = no, [NotificationText.Keys.Stops] = "B2 (14.03.2)" };

        await using (var s = At(Monday))
        {
            // Push only when switched on and the driver is signed in to the app.
            await NotificationQueue.ForAppointmentAsync(s.Db, id, NotificationTemplates.GateIn, values, NotificationChannel.Sms, Monday.GetUtcNow(), default, includeBooker: false, pushFirst: true);
            await s.Db.SaveChangesAsync();
        }
        await AppWithPushAsync("fcm-token-abdullah");
        await using (var s = At(Monday))
        {
            await NotificationQueue.ForAppointmentAsync(s.Db, id, NotificationTemplates.GateIn, values, NotificationChannel.Sms, Monday.GetUtcNow(), default, includeBooker: false, pushFirst: false);
            await NotificationQueue.ForAppointmentAsync(s.Db, id, NotificationTemplates.GateIn, values, NotificationChannel.Sms, Monday.GetUtcNow(), default, includeBooker: false, pushFirst: true);
            await s.Db.SaveChangesAsync();
        }
        Assert.Equal(new[] { NotificationChannel.Sms, NotificationChannel.Sms, NotificationChannel.Push }, (await LogAsync()).Select(n => n.Channel).ToArray());

        await using (var s = At(Clock(19, 12, 1)))
            Assert.Equal(new DispatchResult(3, 0, 0), await s.Dispatcher.DispatchDueAsync());
        var (to, push) = Assert.Single(_push.Sent);
        Assert.Equal("fcm-token-abdullah", to);
        Assert.Contains("B2 (14.03.2)", push.Body);
        Assert.Equal(NotificationTemplates.GateIn, push.Data!["template"]);
        Assert.Equal(no, push.Data["appointmentNo"]);
    }

    [Fact]
    public async Task A_push_to_a_removed_app_goes_by_sms_instead_and_the_token_is_dropped()
    {
        if (ConnectionString is null)
            return;

        var (id, no) = await BookAsync();
        var deviceId = await AppWithPushAsync("fcm-token-gone");
        await using (var s = At(Monday))
        {
            await NotificationQueue.ForAppointmentAsync(s.Db, id, NotificationTemplates.GateIn,
                new Dictionary<string, string> { [NotificationText.Keys.AppointmentNo] = no, [NotificationText.Keys.Stops] = "B2" },
                NotificationChannel.Sms, Monday.GetUtcNow(), default, includeBooker: false, pushFirst: true);
            await s.Db.SaveChangesAsync();
        }

        _push.FailWith = new NotificationSendException("UNREGISTERED", permanent: true, invalidRecipient: true);
        await using (var s = At(Clock(19, 12, 1)))
            Assert.Equal(new DispatchResult(0, 0, 1), await s.Dispatcher.DispatchDueAsync());

        var log = await LogAsync();
        Assert.Equal(NotificationStatus.Failed, log[0].Status);
        Assert.Contains("Sent by Sms instead", log[0].LastError);
        Assert.Equal((NotificationChannel.Sms, NotificationStatus.Queued), (log[1].Channel, log[1].Status));
        await using (var s = At(Monday))
            Assert.Null(await s.Db.DriverDevices.Where(d => d.DeviceId == deviceId).Select(d => d.PushToken).SingleAsync());

        await using (var s = At(Clock(19, 12, 2)))
            Assert.Equal(new DispatchResult(1, 0, 0), await s.Dispatcher.DispatchDueAsync());
        var (to, sms) = Assert.Single(_sms.Sent);
        Assert.Equal("+966500000001", to);
        Assert.Contains("B2", sms.Body);
    }

    [Fact]
    public async Task On_hold_messages_reach_the_driver_in_arabic_and_the_dispatcher_by_email()
    {
        if (ConnectionString is null)
            return;

        var (_, no) = await BookAndHoldAsync();

        await using (var s = At(Clock(19, 12, 1)))
            Assert.Equal(new DispatchResult(2, 0, 0), await s.Dispatcher.DispatchDueAsync());

        var (to, sms) = Assert.Single(_sms.Sent);
        Assert.Equal("+966500000001", to);
        Assert.Contains(no, sms.Body);
        Assert.Contains("موقوف", sms.Body);
        Assert.Contains("Customs hold on MSCU1234566", sms.Body);
        Assert.Contains("Tue 20 Oct 10:00–11:00", sms.Body);

        var (mailTo, mail) = Assert.Single(_email.Sent);
        Assert.Equal(DemoDataSeeder.DispatcherEmail, mailTo);
        Assert.StartsWith("Yardsyncs — Appointment", mail.Subject);

        var log = await LogAsync();
        Assert.All(log, n => Assert.Equal(NotificationStatus.Sent, n.Status));
        Assert.Contains(log, n => n.Recipient == "+966500000001" && n.ProviderRef == "msg-1" && n.Attempts == 1);

        // Nothing left to send.
        await using (var s = At(Clock(19, 12, 2)))
            Assert.Equal(new DispatchResult(0, 0, 0), await s.Dispatcher.DispatchDueAsync());
    }

    [Fact]
    public async Task Temporary_failures_are_retried_with_backoff_and_permanent_ones_fail()
    {
        if (ConnectionString is null)
            return;

        await BookAndHoldAsync();
        _sms.FailWith = new NotificationSendException("Gateway timeout", permanent: false);
        _email.FailWith = new NotificationSendException("Mailbox does not exist", permanent: true);

        await using (var s = At(Clock(19, 12, 1)))
            Assert.Equal(new DispatchResult(0, 1, 1), await s.Dispatcher.DispatchDueAsync());

        var log = await LogAsync();
        var sms = log.Single(n => n.Channel == NotificationChannel.Sms);
        Assert.Equal(NotificationStatus.Queued, sms.Status);
        Assert.Equal(1, sms.Attempts);
        Assert.Equal(Clock(19, 12, 2).GetUtcNow(), sms.NextAttemptAt); // retry after 1 minute
        Assert.Contains("Gateway timeout", sms.LastError);
        var mail = log.Single(n => n.Channel == NotificationChannel.Email);
        Assert.Equal(NotificationStatus.Failed, mail.Status);
        Assert.Contains("Mailbox does not exist", mail.LastError);

        // Not due yet 30 s later; sent once the provider is back after the backoff.
        _sms.FailWith = null;
        await using (var s = At(Clock(19, 12, 1)))
            Assert.Equal(new DispatchResult(0, 0, 0), await s.Dispatcher.DispatchDueAsync());
        await using (var s = At(Clock(19, 12, 3)))
            Assert.Equal(new DispatchResult(1, 0, 0), await s.Dispatcher.DispatchDueAsync());
        Assert.Equal(2, (await LogAsync()).Single(n => n.Channel == NotificationChannel.Sms).Attempts);
    }

    [Fact]
    public async Task Old_messages_expire_instead_of_arriving_late()
    {
        if (ConnectionString is null)
            return;

        await BookAndHoldAsync();

        await using (var s = At(Clock(20, 1)))   // 13 hours later
            Assert.Equal(new DispatchResult(0, 0, 2), await s.Dispatcher.DispatchDueAsync());

        Assert.Empty(_sms.Sent);
        Assert.All(await LogAsync(), n => Assert.StartsWith("Expired", n.LastError));
    }

    [Fact]
    public async Task Drivers_get_one_reminder_before_their_slot()
    {
        if (ConnectionString is null)
            return;

        var (_, no) = await BookAsync();

        await using (var s = At(Clock(20, 7)))   // 3 h before: too early
            Assert.Equal(0, await s.Dispatcher.QueueRemindersAsync());

        await using (var s = At(Clock(20, 8, 30)))
        {
            Assert.Equal(1, await s.Dispatcher.QueueRemindersAsync());
            Assert.Equal(0, await s.Dispatcher.QueueRemindersAsync()); // only once
            Assert.Equal(new DispatchResult(1, 0, 0), await s.Dispatcher.DispatchDueAsync());
        }

        var (to, sms) = Assert.Single(_sms.Sent);
        Assert.Equal("+966500000001", to);
        Assert.Contains(no, sms.Body);
        Assert.Contains("09:30", sms.Body);          // gate opens 30 min before the slot
        Assert.Contains("MSCU1234566", sms.Body);
        Assert.Empty(_email.Sent);                   // reminders go to the driver only
    }

    [Fact]
    public async Task Closing_booked_hours_tells_drivers_and_dispatchers_to_reschedule()
    {
        if (ConnectionString is null)
            return;

        var (_, no) = await BookAsync();
        var planner = new CurrentUser(_demo.PlannerUserId, UserRole.Planner, null, _demo.TerminalId);

        await using (var s = At(Monday))
        {
            var closed = await s.Capacity.CreateOverrideAsync(planner, _demo.TerminalId, new OverrideInput(
                new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.FromHours(3)),
                new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.FromHours(3)),
                null, OverrideAction.Close, null, "Crane 3 breakdown"));
            Assert.Equal(AdminStatus.Created, closed.Status);
        }

        var log = await LogAsync();
        Assert.Equal(2, log.Count);
        Assert.All(log, n => Assert.Equal(NotificationTemplates.SlotClosed, n.TemplateCode));
        Assert.All(log, n => Assert.Contains("Crane 3 breakdown", n.Params));

        await using (var s = At(Clock(19, 12, 1)))
            await s.Dispatcher.DispatchDueAsync();
        var (_, sms) = Assert.Single(_sms.Sent);
        Assert.Contains(no, sms.Body);
        Assert.Contains("Crane 3 breakdown", sms.Body);
    }
}
