using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tas.Domain;
using Tas.Infrastructure.Persistence;

namespace Tas.Infrastructure.Integration;

/// <summary>
/// Configuration section "Integration". Per terminal (by terminal code) a webhook that receives the events it
/// subscribes to. The secret belongs in an environment variable, e.g. Integration__Terminals__DEMO__Secret.
/// </summary>
public sealed class IntegrationOptions
{
    public bool Enabled { get; set; } = true;
    public int IntervalSeconds { get; set; } = 10;
    public int BatchSize { get; set; } = 50;
    public int MaxAttempts { get; set; } = 8;

    /// <summary>Events not delivered within this many hours are given up (the visit is long over).</summary>
    public int ExpireHours { get; set; } = 24;

    public int TimeoutSeconds { get; set; } = 10;

    public Dictionary<string, TerminalWebhook> Terminals { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class TerminalWebhook
{
    /// <summary>HTTPS endpoint of the terminal's system; empty = the terminal takes no events.</summary>
    public string Url { get; set; } = "";

    /// <summary>Shared secret for the signature header.</summary>
    public string Secret { get; set; } = "";

    /// <summary>Event types to send; empty = all.</summary>
    public List<string> Events { get; set; } = [];

    public bool Wants(string eventType) => Url.Length > 0 && (Events.Count == 0 || Events.Contains(eventType, StringComparer.OrdinalIgnoreCase));
}

public sealed record IntegrationEventView(
    long EventId, string EventType, string? AppointmentNo, string Status, int Attempts, string? LastError,
    DateTimeOffset CreatedAt, DateTimeOffset? SentAt, JsonElement Payload);

public static class IntegrationEventQueue
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Adds the event to the outbox; saved with the caller's change.</summary>
    public static IntegrationEvent Add(TasDbContext db, int terminalId, string eventType, long? appointmentId, object payload, DateTimeOffset now)
    {
        var row = new IntegrationEvent
        {
            TerminalId = terminalId,
            EventType = eventType,
            AppointmentId = appointmentId,
            Payload = JsonSerializer.Serialize(payload, Json),
            Status = IntegrationEventStatus.Queued,
            CreatedAt = now,
        };
        db.IntegrationEvents.Add(row);
        return row;
    }
}

public sealed record IntegrationDispatchResult(int Sent, int Retrying, int Failed, int Skipped);

/// <summary>
/// Delivers the outbox: POST of the event as JSON to the terminal's webhook with
/// X-Yardsyncs-Event, X-Yardsyncs-Delivery (event id, the same on retries — use it to ignore duplicates) and
/// X-Yardsyncs-Signature "t={unix seconds},v1={hex HMAC-SHA256 of "{t}.{body}" with the secret}".
/// 2xx = delivered; other answers are retried after 30 s, 2, 10, 30 and 60 min, except 4xx (not 408 / 429) which fail.
/// </summary>
public sealed class IntegrationDispatcher(
    TasDbContext db, IHttpClientFactory http, IOptionsMonitor<IntegrationOptions> options, TimeProvider clock, ILogger<IntegrationDispatcher> logger)
{
    private static readonly TimeSpan[] Backoff =
        [TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(60)];

    private static readonly TimeSpan ClaimFor = TimeSpan.FromMinutes(2);

    public async Task<IntegrationDispatchResult> DispatchDueAsync(CancellationToken ct = default)
    {
        var o = options.CurrentValue;
        var now = clock.GetUtcNow();
        var due = await db.IntegrationEvents.AsNoTracking()
            .Where(e => e.Status == IntegrationEventStatus.Queued && (e.NextAttemptAt == null || e.NextAttemptAt <= now))
            .OrderBy(e => e.EventId)
            .Select(e => e.EventId)
            .Take(Math.Max(1, o.BatchSize))
            .ToListAsync(ct);

        int sent = 0, retrying = 0, failed = 0, skipped = 0;
        var codes = new Dictionary<int, string>();
        foreach (var id in due)
        {
            var claimed = await db.IntegrationEvents
                .Where(e => e.EventId == id && e.Status == IntegrationEventStatus.Queued && (e.NextAttemptAt == null || e.NextAttemptAt <= now))
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.NextAttemptAt, now + ClaimFor), ct);
            if (claimed == 0)
                continue;

            switch (await SendOneAsync(id, o, now, codes, ct))
            {
                case IntegrationEventStatus.Sent: sent++; break;
                case IntegrationEventStatus.Failed: failed++; break;
                case IntegrationEventStatus.Skipped: skipped++; break;
                default: retrying++; break;
            }
        }

        if (sent + retrying + failed > 0)
            logger.LogInformation("Integration events: {Sent} sent, {Retrying} to retry, {Failed} failed", sent, retrying, failed);
        return new IntegrationDispatchResult(sent, retrying, failed, skipped);
    }

    private async Task<string> SendOneAsync(long id, IntegrationOptions o, DateTimeOffset now, Dictionary<int, string> codes, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        var e = await db.IntegrationEvents.SingleAsync(x => x.EventId == id, ct);
        if (!codes.TryGetValue(e.TerminalId, out var code))
            codes[e.TerminalId] = code = await db.Terminals.AsNoTracking().Where(t => t.TerminalId == e.TerminalId).Select(t => t.Code).SingleAsync(ct);

        string Finish(string status, string? error = null)
        {
            e.Status = status;
            e.LastError = error is null ? null : Truncate(error, 400);
            e.NextAttemptAt = null;
            if (status == IntegrationEventStatus.Sent)
                e.SentAt = clock.GetUtcNow();
            return status;
        }

        string result;
        var hook = o.Terminals.FirstOrDefault(t => string.Equals(t.Key, code, StringComparison.OrdinalIgnoreCase)).Value;
        if (hook is null || !hook.Wants(e.EventType))
        {
            result = Finish(IntegrationEventStatus.Skipped);
        }
        else if (e.CreatedAt < now.AddHours(-Math.Max(1, o.ExpireHours)))
        {
            result = Finish(IntegrationEventStatus.Failed, $"Expired: not delivered within {o.ExpireHours} h.");
        }
        else
        {
            e.Attempts++;
            var body = Body(e);
            var (ok, permanent, error) = await PostAsync(hook, e, body, now, o, ct);
            if (ok)
                result = Finish(IntegrationEventStatus.Sent);
            else if (permanent || e.Attempts >= o.MaxAttempts)
                result = Finish(IntegrationEventStatus.Failed, error);
            else
            {
                e.LastError = Truncate(error, 400);
                e.NextAttemptAt = now + Backoff[Math.Min(e.Attempts - 1, Backoff.Length - 1)];
                result = IntegrationEventStatus.Queued;
            }
        }

        await db.SaveChangesAsync(ct);
        return result;
    }

    /// <summary>The stored payload with the event id and type in front.</summary>
    public static string Body(IntegrationEvent e)
    {
        var payload = JsonNode.Parse(e.Payload) as JsonObject ?? new JsonObject();
        var body = new JsonObject { ["eventId"] = e.EventId, ["type"] = e.EventType };
        foreach (var (key, value) in payload.ToList())
        {
            if (key is "eventId" or "type")
                continue;
            payload.Remove(key);
            body[key] = value;
        }
        return body.ToJsonString();
    }

    /// <summary>"t=…,v1=…": HMAC-SHA256 over "{t}.{body}", so the receiver can check origin and age.</summary>
    public static string Signature(string secret, long unixSeconds, string body)
    {
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{unixSeconds}.{body}"));
        return $"t={unixSeconds},v1={Convert.ToHexStringLower(mac)}";
    }

    private async Task<(bool Ok, bool Permanent, string? Error)> PostAsync(
        TerminalWebhook hook, IntegrationEvent e, string body, DateTimeOffset now, IntegrationOptions o, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, hook.Url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Yardsyncs-Event", e.EventType);
        request.Headers.Add("X-Yardsyncs-Delivery", e.EventId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (hook.Secret.Length > 0)
            request.Headers.Add("X-Yardsyncs-Signature", Signature(hook.Secret, now.ToUnixTimeSeconds(), body));

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, o.TimeoutSeconds)));
            using var response = await http.CreateClient("integration").SendAsync(request, timeout.Token);
            var status = (int)response.StatusCode;
            if (status is >= 200 and < 300)
                return (true, false, null);
            var permanent = status is >= 400 and < 500 && response.StatusCode is not (HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests);
            return (false, permanent, $"Webhook answered {status}.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (false, false, "Webhook did not answer in time.");
        }
        catch (HttpRequestException ex)
        {
            return (false, false, $"Webhook could not be reached: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            return (false, true, $"Webhook URL is not usable: {ex.Message}");
        }
    }

    /// <summary>Recent events of a terminal, newest first, for the control room and for checking an integration.</summary>
    public async Task<IReadOnlyList<IntegrationEventView>> ListAsync(int terminalId, int limit, CancellationToken ct = default)
    {
        var rows = await db.IntegrationEvents.AsNoTracking()
            .Where(e => e.TerminalId == terminalId)
            .OrderByDescending(e => e.EventId)
            .Take(Math.Clamp(limit, 1, 500))
            .Select(e => new
            {
                e.EventId, e.EventType, e.Status, e.Attempts, e.LastError, e.CreatedAt, e.SentAt, e.Payload,
                AppointmentNo = db.Appointments.Where(a => a.AppointmentId == e.AppointmentId).Select(a => a.AppointmentNo).FirstOrDefault(),
            })
            .ToListAsync(ct);
        return rows.Select(r => new IntegrationEventView(r.EventId, r.EventType, r.AppointmentNo, r.Status, r.Attempts, r.LastError,
            r.CreatedAt, r.SentAt, JsonDocument.Parse(r.Payload).RootElement.Clone())).ToList();
    }

    private static string? Truncate(string? value, int max) => value is null ? null : value.Length <= max ? value : value[..max];
}

/// <summary>Delivers integration events every Integration:IntervalSeconds (default 10).</summary>
public sealed class IntegrationWorker(
    IServiceScopeFactory scopes, IOptionsMonitor<IntegrationOptions> options, ILogger<IntegrationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.CurrentValue.Enabled)
            return;

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(2, options.CurrentValue.IntervalSeconds)));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<IntegrationDispatcher>().DispatchDueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Integration event run failed");
            }
        }
    }
}
