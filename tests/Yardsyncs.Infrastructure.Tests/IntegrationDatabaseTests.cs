using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Yardsyncs.Domain;
using Yardsyncs.Infrastructure.Auth;
using Yardsyncs.Infrastructure.Capacity;
using Yardsyncs.Infrastructure.Integration;
using Yardsyncs.Infrastructure.Persistence;

namespace Yardsyncs.Infrastructure.Tests;

/// <summary>Delivery of integration events to a terminal's webhook (fake HTTP server, PostgreSQL outbox).</summary>
public sealed class IntegrationDatabaseTests : IAsyncLifetime
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("TAS_TEST_DB");
    private static readonly DateTimeOffset T0 = new(2026, 10, 19, 12, 0, 0, TimeSpan.FromHours(3));
    private int _terminalId;

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

    private sealed class Webhook(params HttpStatusCode[] answers) : HttpMessageHandler, IHttpClientFactory
    {
        private int _next;
        public List<(HttpRequestMessage Request, string Body)> Received { get; } = [];

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Received.Add((request, await request.Content!.ReadAsStringAsync(ct)));
            return new HttpResponseMessage(answers[Math.Min(_next++, answers.Length - 1)]);
        }
    }

    private static YardsyncsDbContext Db() => new(new DbContextOptionsBuilder<YardsyncsDbContext>().UseNpgsql(ConnectionString).Options);

    private static IntegrationOptions Hooked(params string[] events) => new()
    {
        Terminals = { ["demo"] = new TerminalWebhook { Url = "https://tos.example.test/yardsyncs", Secret = "s3cret", Events = [.. events] } },
    };

    private static IntegrationDispatcher Dispatcher(YardsyncsDbContext db, Webhook hook, IntegrationOptions o, DateTimeOffset now) =>
        new(db, hook, new StaticOptions<IntegrationOptions>(o), new FixedClock(now), NullLogger<IntegrationDispatcher>.Instance);

    public async Task InitializeAsync()
    {
        if (ConnectionString is null)
        {
            if (Environment.GetEnvironmentVariable("CI") == "true")
                throw new InvalidOperationException("TAS_TEST_DB is not set in CI.");
            return;
        }
        await using var db = Db();
        await db.Database.ExecuteSqlRawAsync("TRUNCATE terminal, company, app_user RESTART IDENTITY CASCADE");
        var clock = new FixedClock(T0);
        var demo = await new DemoDataSeeder(db, new SlotGenerationService(db, clock, NullLogger<SlotGenerationService>.Instance), new PasswordService(), clock).SeedAsync();
        _terminalId = demo.TerminalId;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<long> QueueAsync(string type, DateTimeOffset? at = null)
    {
        await using var db = Db();
        var row = IntegrationEventQueue.Add(db, _terminalId, type, null,
            new { occurredAt = at ?? T0, terminal = "DEMO", appointmentNo = "DEMO-1", stop = new { block = "B2", slot = "14.03.2" } }, at ?? T0);
        await db.SaveChangesAsync();
        return row.EventId;
    }

    private static async Task<IntegrationEvent> RowAsync(long id)
    {
        await using var db = Db();
        return await db.IntegrationEvents.AsNoTracking().SingleAsync(e => e.EventId == id);
    }

    [Fact]
    public async Task Subscribed_events_are_signed_and_delivered_the_rest_kept()
    {
        if (ConnectionString is null)
            return;

        var atBlock = await QueueAsync(IntegrationEventTypes.TruckAtBlock);
        var done = await QueueAsync(IntegrationEventTypes.StopCompleted);
        var hook = new Webhook(HttpStatusCode.Accepted);
        var now = T0.AddSeconds(5);

        await using (var db = Db())
            Assert.Equal(new IntegrationDispatchResult(1, 0, 0, 1), await Dispatcher(db, hook, Hooked("TRUCK_AT_BLOCK"), now).DispatchDueAsync());

        var (request, body) = Assert.Single(hook.Received);
        Assert.Equal("https://tos.example.test/yardsyncs", request.RequestUri!.ToString());
        Assert.Equal("TRUCK_AT_BLOCK", request.Headers.GetValues("X-Yardsyncs-Event").Single());
        Assert.Equal(atBlock.ToString(), request.Headers.GetValues("X-Yardsyncs-Delivery").Single());
        Assert.Equal(IntegrationDispatcher.Signature("s3cret", now.ToUnixTimeSeconds(), body), request.Headers.GetValues("X-Yardsyncs-Signature").Single());
        Assert.StartsWith($"t={now.ToUnixTimeSeconds()},v1=", request.Headers.GetValues("X-Yardsyncs-Signature").Single());
        using (var json = JsonDocument.Parse(body))
        {
            Assert.Equal(atBlock, json.RootElement.GetProperty("eventId").GetInt64());
            Assert.Equal("TRUCK_AT_BLOCK", json.RootElement.GetProperty("type").GetString());
            Assert.Equal("B2", json.RootElement.GetProperty("stop").GetProperty("block").GetString());
        }

        Assert.Equal(IntegrationEventStatus.Sent, (await RowAsync(atBlock)).Status);
        Assert.Equal(IntegrationEventStatus.Skipped, (await RowAsync(done)).Status);
    }

    [Fact]
    public async Task Outages_are_retried_and_refusals_fail()
    {
        if (ConnectionString is null)
            return;

        var first = await QueueAsync(IntegrationEventTypes.TruckAtBlock);
        var hook = new Webhook(HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);
        var o = Hooked();

        await using (var db = Db())
            Assert.Equal(new IntegrationDispatchResult(0, 1, 0, 0), await Dispatcher(db, hook, o, T0).DispatchDueAsync());
        var row = await RowAsync(first);
        Assert.Equal(IntegrationEventStatus.Queued, row.Status);
        Assert.Equal(T0.AddSeconds(30), row.NextAttemptAt);
        Assert.Contains("503", row.LastError);

        // Not before the backoff, then delivered.
        await using (var db = Db())
            Assert.Equal(new IntegrationDispatchResult(0, 0, 0, 0), await Dispatcher(db, hook, o, T0.AddSeconds(10)).DispatchDueAsync());
        await using (var db = Db())
            Assert.Equal(new IntegrationDispatchResult(1, 0, 0, 0), await Dispatcher(db, hook, o, T0.AddSeconds(31)).DispatchDueAsync());
        Assert.Equal(2, (await RowAsync(first)).Attempts);

        // A 400 won't get better by retrying.
        var refused = await QueueAsync(IntegrationEventTypes.TruckCheckedIn);
        await using (var db = Db())
            Assert.Equal(new IntegrationDispatchResult(0, 0, 1, 0), await Dispatcher(db, new Webhook(HttpStatusCode.BadRequest), o, T0.AddMinutes(1)).DispatchDueAsync());
        Assert.Equal(IntegrationEventStatus.Failed, (await RowAsync(refused)).Status);

        // A terminal without a webhook keeps its events as SKIPPED.
        var unhooked = await QueueAsync(IntegrationEventTypes.TruckAtBlock);
        await using (var db = Db())
            Assert.Equal(new IntegrationDispatchResult(0, 0, 0, 1), await Dispatcher(db, new Webhook(HttpStatusCode.OK), new IntegrationOptions(), T0.AddMinutes(2)).DispatchDueAsync());
        Assert.Equal(IntegrationEventStatus.Skipped, (await RowAsync(unhooked)).Status);
    }

    [Fact]
    public void The_signature_is_an_hmac_of_time_and_body()
    {
        var body = """{"eventId":1,"type":"TRUCK_AT_BLOCK"}""";
        var expected = Convert.ToHexStringLower(System.Security.Cryptography.HMACSHA256.HashData(
            Encoding.UTF8.GetBytes("s3cret"), Encoding.UTF8.GetBytes($"1760864400.{body}")));
        Assert.Equal($"t=1760864400,v1={expected}", IntegrationDispatcher.Signature("s3cret", 1760864400, body));
        Assert.StartsWith("""{"eventId":7,"type":"STOP_COMPLETED","a":1""", IntegrationDispatcher.Body(new IntegrationEvent
        {
            EventId = 7, EventType = "STOP_COMPLETED", Payload = """{"a":1,"type":"ignored","eventId":99}""",
        }));
    }
}
