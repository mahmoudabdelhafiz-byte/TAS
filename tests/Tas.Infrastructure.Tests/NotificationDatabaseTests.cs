using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Tas.Application.Booking;
using Tas.Domain;
using Tas.Domain.Booking;
using Tas.Domain.Capacity;
using Tas.Infrastructure.Auth;
using Tas.Infrastructure.Booking;
using Tas.Infrastructure.Capacity;
using Tas.Infrastructure.Notifications;
using Tas.Infrastructure.Persistence;
using Tas.Infrastructure.Tos;

namespace Tas.Infrastructure.Tests;

/// <summary>
/// Delivering the notification outbox: rendering, recipients, retries, skipping, reminders and slot-closed
/// messages. "Now" is Monday 2026-10-19 12:00 Riyadh unless a test moves the clock.
/// </summary>
public sealed class NotificationDatabaseTests : IAsyncLifetime
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("TAS_TEST_DB");
    private static readonly TimeProvider Monday = Clock(19, 12);

    private readonly FakeTosOptions _tos = new();
    private DemoSeedResult _demo = null!;

    private static TimeProvider Clock(int day, int riyadhHour, int minute = 0) =>
        new FixedClock(new DateTimeOffset(2026, 10, day, riyadhHour, minute, 0, TimeSpan.FromHours(3)));

    private static DateTimeOffset Riyadh(int day, int hour) => new(2026, 10, day, hour, 0, 0, TimeSpan.FromHours(3));

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

    /// <summary>Records what would have been sent; answers with the configured result.</summary>
    private sealed class FakeChannel(NotificationChannel channel, Func<OutgoingMessage, DeliveryResult>? answer = null) : INotificationChannel
    {
        public ConcurrentQueue<OutgoingMessage> Sent { get; } = new();
        public NotificationChannel Channel => channel;

        public Task<DeliveryResult> SendAsync(OutgoingMessage message, CancellationToken ct)
        {
            Sent.Enqueue(message);
            return Task.FromResult(answer?.Invoke(message) ?? DeliveryResult.Sent($"ref-{message.NotificationId}"));
        }
    }

    private sealed class Services(TimeProvider clock, FakeTosOptions tosOptions) : IAsyncDisposable
    {
        private readonly FakeTosAdapter _tos = new(new StaticOptions<FakeTosOptions>(tosOptions));
        public TasDbContext Db { get; } = new(new DbContextOptionsBuilder<TasDbContext>().UseNpgsql(ConnectionString).Options);
        public NotificationOptions Options { get; } = new() { MaxAttempts = 3, BatchSize = 50 };
        public AppointmentQueryService Queries => new(Db, clock);
        public BookingService Booking => new(Db, new BookingCounterStore(Db), _tos, Queries, clock, NullLogger<BookingService>.Instance);
        public RevalidationService Revalidation => new(Db, _tos, Queries, clock, new ConfigurationBuilder().Build(), NullLogger<RevalidationService>.Instance);
        public SlotGenerationService Slots => new(Db, clock, NullLogger<SlotGenerationService>.Instance);
        public SlotReminderService Reminders => new(Db, Microsoft.Extensions.Options.Options.Create(Options), clock, NullLogger<SlotReminderService>.Instance);
        public NotificationQueryService Outbox => new(Db, clock);

        public NotificationSender Sender(params INotificationChannel[] channels) =>
            new(Db, channels, Microsoft.Extensions.Options.Options.Create(Options), clock, NullLogger<NotificationSender>.Instance);

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private Services At(TimeProvider clock) => new(clock, _tos);

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
        _demo = await new DemoDataSeeder(s.Db, s.Slots, new PasswordService(), Monday).SeedAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<(long Id, string No)> BookAsync(int day, int hour, string container, TimeProvider? at = null, int fleet = 0, bool assign = true)
    {
        await using var s = At(at ?? Monday);
        var start = Riyadh(day, hour).ToUniversalTime();
        var slotId = await s.Db.Slots.Where(x => x.TerminalId == _demo.TerminalId && x.StartAt == start).Select(x => x.SlotId).SingleAsync();
        var outcome = await s.Booking.CreateAsync(_demo.TerminalId, new BookingRequest(
            _demo.CompanyId, _demo.DispatcherUserId, slotId,
            assign ? _demo.TruckIds[fleet] : null, assign ? _demo.DriverIds[fleet] : null,
            [new BookingItemRequest("IMP_PICKUP", ContainerNo: container)]));
        Assert.Equal(BookingOutcomeKind.Created, outcome.Kind);
        var id = await s.Db.Appointments.Where(a => a.AppointmentNo == outcome.Appointment!.AppointmentNo).Select(a => a.AppointmentId).SingleAsync();
        return (id, outcome.Appointment!.AppointmentNo);
    }

    /// <summary>Books Tuesday 10:00 and puts a customs hold on the container: STATUS_RED queued for driver (SMS) and booker (email).</summary>
    private async Task<(long Id, string No)> BookAndHoldAsync(string container = "MSCU1234566")
    {
        var booked = await BookAsync(20, 10, container);
        _tos.CustomsHold.Add(container);
        await using var s = At(Monday);
        Assert.Equal(TrafficLight.Red, (await s.Revalidation.RevalidateAsync(booked.Id, "scheduled"))!.Light);
        return booked;
    }

    private static Task<List<NotificationLog>> RowsAsync(Services s, long appointmentId) =>
        s.Db.NotificationLogs.AsNoTracking().Where(n => n.AppointmentId == appointmentId).OrderBy(n => n.NotificationId).ToListAsync();

    [Fact]
    public async Task A_hold_is_sent_to_the_driver_by_sms_in_arabic_and_to_the_booker_by_email()
    {
        if (ConnectionString is null) return;

        var (id, no) = await BookAndHoldAsync();
        var sms = new FakeChannel(NotificationChannel.Sms);
        var email = new FakeChannel(NotificationChannel.Email);

        await using (var s = At(Monday))
        {
            var run = await s.Sender(sms, email).RunAsync();
            Assert.Equal(new NotificationRunResult(2, 2, 0, 0, 0), run);

            var text = Assert.Single(sms.Sent);
            Assert.Equal("+966500000001", text.To);
            Assert.Equal("ar", text.Message.Language);
            Assert.Contains(no, text.Message.Body);
            Assert.Contains("MSCU1234566 (حجز جمركي)", text.Message.Body);
            Assert.Contains("20/10 10:00-11:00", text.Message.Body); // Riyadh local time

            var mail = Assert.Single(email.Sent);
            Assert.Equal(DemoDataSeeder.DispatcherEmail, mail.To);
            Assert.Equal("en", mail.Message.Language);
            Assert.Contains(no, mail.Message.Subject);
            Assert.Contains("MSCU1234566 (customs hold)", mail.Message.Body);
            Assert.Contains("1234 ABJ", mail.Message.Body);

            var rows = await RowsAsync(s, id);
            Assert.All(rows, r =>
            {
                Assert.Equal(NotificationStatus.Sent, r.Status);
                Assert.Equal(1, r.Attempts);
                Assert.Equal($"ref-{r.NotificationId}", r.ProviderRef);
                Assert.NotNull(r.SentAt);
                Assert.Null(r.NextAttemptAt);
            });
            Assert.Contains(rows, r => r.Recipient == "+966500000001");
            Assert.Contains(rows, r => r.Recipient == DemoDataSeeder.DispatcherEmail);
        }

        // Nothing left to send.
        await using (var s = At(Monday))
            Assert.Equal(0, (await s.Sender(sms, email).RunAsync()).Claimed);
        Assert.Single(sms.Sent);
    }

    [Fact]
    public async Task Transient_errors_are_retried_with_back_off_until_the_attempts_run_out()
    {
        if (ConnectionString is null) return;

        var (id, _) = await BookAndHoldAsync();
        var down = new FakeChannel(NotificationChannel.Sms, _ => DeliveryResult.Retry("gateway 503"));
        var email = new FakeChannel(NotificationChannel.Email);

        NotificationLog Sms(List<NotificationLog> rows) => rows.Single(r => r.Channel == NotificationChannel.Sms);

        await using (var s = At(Monday))
        {
            Assert.Equal(new NotificationRunResult(2, 1, 1, 0, 0), await s.Sender(down, email).RunAsync());
            var sms = Sms(await RowsAsync(s, id));
            Assert.Equal(NotificationStatus.Queued, sms.Status);
            Assert.Equal(1, sms.Attempts);
            Assert.Equal("gateway 503", sms.LastError);
            Assert.Equal(Riyadh(19, 12).AddMinutes(1), sms.NextAttemptAt);
        }

        // Not due yet.
        await using (var s = At(Clock(19, 12, 0)))
            Assert.Equal(0, (await s.Sender(down, email).RunAsync()).Claimed);

        await using (var s = At(Clock(19, 12, 1)))
        {
            Assert.Equal(1, (await s.Sender(down, email).RunAsync()).Retrying);
            var sms = Sms(await RowsAsync(s, id));
            Assert.Equal(2, sms.Attempts);
            Assert.Equal(Riyadh(19, 12).AddMinutes(1 + 5), sms.NextAttemptAt);
        }

        // Third attempt = MaxAttempts: give up.
        await using (var s = At(Clock(19, 12, 6)))
        {
            Assert.Equal(1, (await s.Sender(down, email).RunAsync()).Failed);
            var sms = Sms(await RowsAsync(s, id));
            Assert.Equal(NotificationStatus.Failed, sms.Status);
            Assert.Equal(3, sms.Attempts);
            Assert.Equal("gateway 503", sms.LastError);
            Assert.Null(sms.NextAttemptAt);
        }

        Assert.Equal(3, down.Sent.Count);
        Assert.Single(email.Sent);
    }

    [Fact]
    public async Task Permanent_errors_fail_at_once_and_staff_can_send_them_again()
    {
        if (ConnectionString is null) return;

        var (id, _) = await BookAndHoldAsync();
        var rejecting = new FakeChannel(NotificationChannel.Sms, _ => DeliveryResult.Fail("Number blacklisted"));

        long smsId;
        await using (var s = At(Monday))
        {
            // No email provider configured: the email fails too, visibly.
            Assert.Equal(new NotificationRunResult(2, 0, 0, 2, 0), await s.Sender(rejecting).RunAsync());
            var rows = await RowsAsync(s, id);
            Assert.All(rows, r => Assert.Equal(NotificationStatus.Failed, r.Status));
            Assert.Equal("Number blacklisted", rows.Single(r => r.Channel == NotificationChannel.Sms).LastError);
            Assert.Contains("No provider is configured for EMAIL", rows.Single(r => r.Channel == NotificationChannel.Email).LastError);
            smsId = rows.Single(r => r.Channel == NotificationChannel.Sms).NotificationId;

            var listed = await s.Outbox.ListAsync(_demo.TerminalId, NotificationStatus.Failed, null, null);
            Assert.Equal(2, listed.Count);
            Assert.Contains(listed, v => v.RecipientType == "Driver" && v.RecipientName == "Abdullah Al-Harbi");
        }

        await using (var s = At(Monday))
        {
            Assert.Equal(RetryStatus.Ok, (await s.Outbox.RetryAsync(_demo.TerminalId, smsId)).Status);
            Assert.Equal(RetryStatus.Conflict, (await s.Outbox.RetryAsync(_demo.TerminalId, smsId)).Status); // already queued
            Assert.Equal(RetryStatus.NotFound, (await s.Outbox.RetryAsync(_demo.TerminalId + 1, smsId)).Status);
        }

        var working = new FakeChannel(NotificationChannel.Sms);
        await using (var s = At(Monday))
        {
            Assert.Equal(1, (await s.Sender(working).RunAsync()).Sent);
            var sms = (await RowsAsync(s, id)).Single(r => r.NotificationId == smsId);
            Assert.Equal(NotificationStatus.Sent, sms.Status);
            Assert.Equal(1, sms.Attempts);
            Assert.Null(sms.LastError);
        }
    }

    [Fact]
    public async Task A_hold_that_clears_before_sending_only_sends_the_all_clear()
    {
        if (ConnectionString is null) return;

        var (id, no) = await BookAndHoldAsync();
        _tos.CustomsHold.Clear();
        await using (var s = At(Monday))
            Assert.NotEmpty((await s.Revalidation.RevalidateAsync(id, "scheduled"))!.Changes);

        var sms = new FakeChannel(NotificationChannel.Sms);
        var email = new FakeChannel(NotificationChannel.Email);
        await using (var s = At(Monday))
        {
            Assert.Equal(new NotificationRunResult(4, 2, 0, 0, 2), await s.Sender(sms, email).RunAsync());
            var rows = await RowsAsync(s, id);
            Assert.All(rows.Where(r => r.TemplateCode == NotificationTemplates.StatusRed), r =>
            {
                Assert.Equal(NotificationStatus.Skipped, r.Status);
                Assert.Contains("Superseded", r.LastError);
            });
            Assert.All(rows.Where(r => r.TemplateCode == NotificationTemplates.StatusCleared), r => Assert.Equal(NotificationStatus.Sent, r.Status));
        }

        Assert.Contains("يمكنك التحرك", Assert.Single(sms.Sent).Message.Body);
        Assert.Contains(no, Assert.Single(email.Sent).Message.Subject);
    }

    [Fact]
    public async Task Messages_for_a_cancelled_appointment_are_not_sent()
    {
        if (ConnectionString is null) return;

        var (id, no) = await BookAndHoldAsync();
        await using (var s = At(Monday))
            Assert.Equal(BookingOutcomeKind.Updated, (await s.Booking.CancelAsync(_demo.TerminalId, no, _demo.DispatcherUserId, "Not needed")).Kind);

        var sms = new FakeChannel(NotificationChannel.Sms);
        var email = new FakeChannel(NotificationChannel.Email);
        await using (var s = At(Monday))
        {
            Assert.Equal(2, (await s.Sender(sms, email).RunAsync()).Skipped);
            Assert.All(await RowsAsync(s, id), r => Assert.Equal("Appointment is Cancelled.", r.LastError));
        }

        Assert.Empty(sms.Sent);
        Assert.Empty(email.Sent);
    }

    [Fact]
    public async Task Reminders_are_queued_once_two_hours_before_the_slot()
    {
        if (ConnectionString is null) return;

        // Booked early on Monday for Monday 13:00 / 14:00 / 15:00.
        var early = Clock(19, 8);
        var (withDriver, _) = await BookAsync(19, 13, "MSCU1234566", early);
        var (noDriver, _) = await BookAsync(19, 14, "MAEU4001110", early, assign: false);
        var (later, _) = await BookAsync(19, 15, "TGHU3000017", early, fleet: 1);
        // Booked at 11:30 for 13:00: already inside the reminder window — no reminder.
        var (lastMinute, _) = await BookAsync(19, 13, "TCLU1112220", Clock(19, 11, 30), fleet: 1);

        await using (var s = At(Monday)) // 12:00: 13:00 and 14:00 are within 2 h
        {
            Assert.Equal(2, await s.Reminders.QueueDueAsync());

            var driverReminder = Assert.Single(await RowsAsync(s, withDriver));
            Assert.Equal(NotificationTemplates.SlotReminder, driverReminder.TemplateCode);
            Assert.Equal(NotificationChannel.Sms, driverReminder.Channel);
            Assert.Equal(_demo.DriverIds[0], driverReminder.DriverId);

            // No driver yet: the dispatcher is reminded instead (to assign one).
            var bookerReminder = Assert.Single(await RowsAsync(s, noDriver));
            Assert.Equal(NotificationChannel.Email, bookerReminder.Channel);
            Assert.Equal(_demo.DispatcherUserId, bookerReminder.UserId);

            Assert.Empty(await RowsAsync(s, later));
            Assert.Empty(await RowsAsync(s, lastMinute));
        }

        await using (var s = At(Clock(19, 12, 30))) // again: nothing new
            Assert.Equal(0, await s.Reminders.QueueDueAsync());

        await using (var s = At(Clock(19, 13, 5))) // 15:00 is now within 2 h
        {
            Assert.Equal(1, await s.Reminders.QueueDueAsync());
            Assert.Equal("ur", Assert.Single(await RowsAsync(s, later)).Language); // the second driver reads Urdu
        }

        var sms = new FakeChannel(NotificationChannel.Sms);
        await using (var s = At(Monday))
        {
            await s.Sender(sms, new FakeChannel(NotificationChannel.Email)).RunAsync();
            Assert.Contains("تذكير", sms.Sent.Single(m => m.To == "+966500000001").Message.Body);
        }
    }

    [Fact]
    public async Task Closing_a_booked_slot_tells_the_driver_and_the_booker_once()
    {
        if (ConnectionString is null) return;

        var (id, _) = await BookAsync(20, 10, "MSCU1234566");

        await using (var s = At(Monday))
        {
            s.Db.CapacityOverrides.Add(new CapacityOverride
            {
                TerminalId = _demo.TerminalId,
                StartAt = Riyadh(20, 10),
                EndAt = Riyadh(20, 11),
                Action = OverrideAction.Close,
                Reason = "Crane 3 maintenance",
                CreatedBy = _demo.PlannerUserId,
                CreatedAt = Riyadh(19, 12),
            });
            await s.Db.SaveChangesAsync();

            var result = await s.Slots.GenerateAsync(_demo.TerminalId, new DateOnly(2026, 10, 20), new DateOnly(2026, 10, 20));
            Assert.Equal(1, result.AppointmentsInClosedSlots);

            var rows = await RowsAsync(s, id);
            Assert.Equal(2, rows.Count);
            Assert.All(rows, r => Assert.Equal(NotificationTemplates.SlotClosed, r.TemplateCode));
            Assert.Contains(rows, r => r.Channel == NotificationChannel.Sms && r.DriverId == _demo.DriverIds[0]);
            Assert.Contains(rows, r => r.Channel == NotificationChannel.Email && r.UserId == _demo.DispatcherUserId);
        }

        // Regenerating again doesn't repeat the message; the booking itself is kept.
        await using (var s = At(Monday))
        {
            Assert.Equal(0, (await s.Slots.GenerateAsync(_demo.TerminalId, new DateOnly(2026, 10, 20), new DateOnly(2026, 10, 20))).AppointmentsInClosedSlots);
            Assert.Equal(2, (await RowsAsync(s, id)).Count);
            Assert.Equal(AppointmentStatus.Confirmed, await s.Db.Appointments.Where(a => a.AppointmentId == id).Select(a => a.Status).SingleAsync());
        }
    }

    [Fact]
    public async Task Two_senders_running_at_the_same_time_never_send_a_message_twice()
    {
        if (ConnectionString is null) return;

        var (id, _) = await BookAsync(20, 10, "MSCU1234566");
        await using (var s = At(Monday))
        {
            for (var i = 0; i < 30; i++)
            {
                s.Db.NotificationLogs.Add(new NotificationLog
                {
                    AppointmentId = id, DriverId = _demo.DriverIds[0], Channel = NotificationChannel.Sms,
                    TemplateCode = NotificationTemplates.SlotClosed, Language = "ar", CreatedAt = Riyadh(19, 11),
                });
            }
            await s.Db.SaveChangesAsync();
        }

        var sms = new FakeChannel(NotificationChannel.Sms);

        async Task DrainAsync()
        {
            await using var s = At(Monday);
            s.Options.BatchSize = 4;
            while ((await s.Sender(sms).RunAsync()).Claimed > 0)
            {
            }
        }

        await Task.WhenAll(DrainAsync(), DrainAsync(), DrainAsync());

        Assert.Equal(30, sms.Sent.Count);
        Assert.Equal(30, sms.Sent.Select(m => m.NotificationId).Distinct().Count());
        await using (var s = At(Monday))
            Assert.All(await RowsAsync(s, id), r => Assert.Equal((NotificationStatus.Sent, 1), (r.Status, r.Attempts)));
    }
}
