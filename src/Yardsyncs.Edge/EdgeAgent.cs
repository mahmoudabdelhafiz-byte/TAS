using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Yardsyncs.Application.Edge;

namespace Yardsyncs.Edge;

/// <summary>The lane agent's settings (configuration Edge:*, or environment variables Edge__*).</summary>
public sealed class EdgeOptions
{
    /// <summary>The cloud installation, e.g. https://yardsyncs.example.com.</summary>
    public string CloudUrl { get; set; } = "";

    /// <summary>A gate system key of the terminal (Lanes → Gate systems). Keep it in an environment variable.</summary>
    public string ApiKey { get; set; } = "";

    public int TerminalId { get; set; }

    /// <summary>Where the snapshot and the passages not yet sent are kept, so a restart loses nothing.</summary>
    public string DataDirectory { get; set; } = "data";

    /// <summary>A cloud that hasn't answered within this is taken as unreachable.</summary>
    public double TimeoutSeconds { get; set; } = 3;

    /// <summary>How often the snapshot is refreshed while the cloud answers.</summary>
    public double SnapshotMinutes { get; set; } = 5;

    public double SettleSeconds { get; set; } = 3;
    public double WindowSeconds { get; set; } = 30;
}

/// <summary>
/// Whether the cloud answers, the snapshot and the engine that runs the lanes without it, kept on disk. All engine
/// calls go through <see cref="Run{T}"/>, one at a time.
/// </summary>
public sealed class EdgeAgent(IOptions<EdgeOptions> options, IHttpClientFactory http, TimeProvider clock, ILogger<EdgeAgent> logger)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    /// <summary>Passages let in but not yet through the barrier are sent after this, as they are.</summary>
    public static readonly TimeSpan SettleBeforeSending = TimeSpan.FromMinutes(2);

    private readonly object _gate = new();
    private EdgeLaneEngine? _engine;

    public EdgeOptions O => options.Value;

    /// <summary>The cloud answered the last call (devices are passed through to it).</summary>
    public bool Online { get; private set; }

    public DateTimeOffset? LastOnlineAt { get; private set; }

    private string _saved = "";

    public T Run<T>(Func<EdgeLaneEngine?, T> action)
    {
        lock (_gate)
        {
            var result = action(_engine);
            if (_engine is not null)
            {
                // Kept on disk whenever a passage was added, went through, or was sent.
                var passages = _engine.Passages;
                var state = $"{passages.Count}:{passages.Count(p => p.PassedAt is not null)}:{passages.FirstOrDefault()?.PassageId}";
                if (state != _saved)
                {
                    SavePassages(passages);
                    _saved = state;
                }
            }
            return result;
        }
    }

    /// <param name="asAgent">With the gate system key, for the agent's own calls (devices send their own keys).</param>
    public HttpClient Cloud(bool asAgent = false)
    {
        var client = http.CreateClient("cloud");
        client.BaseAddress = new Uri(O.CloudUrl.TrimEnd('/') + "/");
        client.Timeout = TimeSpan.FromSeconds(asAgent ? Math.Max(O.TimeoutSeconds, 30) : O.TimeoutSeconds);
        if (asAgent)
            client.DefaultRequestHeaders.Add("X-Api-Key", O.ApiKey);
        return client;
    }

    public void CloudAnswered()
    {
        if (!Online)
            logger.LogInformation("The cloud answers again: lane devices are passed through to it");
        Online = true;
        LastOnlineAt = clock.GetUtcNow();
    }

    public void CloudUnreachable(Exception? ex = null)
    {
        if (Online || LastOnlineAt is null)
            logger.LogWarning(ex, "The cloud can't be reached: the automatic lanes run here from the snapshot taken at {TakenAt}",
                Run(e => e?.Snapshot.TakenAt));
        Online = false;
    }

    // ================================================================ snapshot and passages

    /// <summary>At start: the last snapshot and the passages not yet sent.</summary>
    public void Load()
    {
        Directory.CreateDirectory(O.DataDirectory);
        var snapshot = Read<EdgeSnapshot>("snapshot.json");
        var passages = Read<List<EdgePassage>>("passages.json") ?? [];
        lock (_gate)
            _engine = snapshot is null ? null : new EdgeLaneEngine(snapshot, passages, O.SettleSeconds, O.WindowSeconds);
        logger.LogInformation("Lane agent for terminal {TerminalId}: snapshot {TakenAt}, {Count} passages to send",
            O.TerminalId, snapshot?.TakenAt, passages.Count);
    }

    /// <summary>A new snapshot (while the cloud answers): the engine starts from it, keeping what isn't sent yet.</summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        var snapshot = await Cloud(asAgent: true).GetFromJsonAsync<EdgeSnapshot>($"api/terminals/{O.TerminalId}/gate/edge/snapshot", Json, ct)
                       ?? throw new InvalidOperationException("Empty snapshot.");
        Write("snapshot.json", snapshot);
        lock (_gate)
            _engine = new EdgeLaneEngine(snapshot, _engine?.Passages ?? Read<List<EdgePassage>>("passages.json") ?? [], O.SettleSeconds, O.WindowSeconds);
        logger.LogInformation("Snapshot taken: {Lanes} lanes, {Devices} devices, {Appointments} appointments",
            snapshot.Lanes.Count, snapshot.Devices.Count, snapshot.Appointments.Count);
    }

    /// <summary>Sends the settled passages; those the cloud recorded (or refused for good) are forgotten.</summary>
    public async Task<int> SendAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var ready = Run(e => (e?.Passages ?? [])
            .Where(p => !p.Admitted || p.PassedAt is not null || now - p.DecidedAt > SettleBeforeSending)
            .Take(500).ToList());
        if (ready.Count == 0)
            return 0;
        var response = await Cloud(asAgent: true).PostAsJsonAsync($"api/terminals/{O.TerminalId}/gate/edge/passages", ready, Json, ct);
        response.EnsureSuccessStatusCode();
        var results = await response.Content.ReadFromJsonAsync<List<EdgePassageResult>>(Json, ct) ?? [];
        foreach (var failed in results.Where(r => r.Outcome == EdgePassageOutcome.Failed))
            logger.LogWarning("Passage {PassageId} not recorded by the cloud: {Detail}", failed.PassageId, failed.Detail);
        Run(e =>
        {
            e?.Forget(results.Select(r => r.PassageId));
            return 0;
        });
        logger.LogInformation("{Count} passages decided here sent to the cloud", results.Count);
        return results.Count;
    }

    private void SavePassages(IReadOnlyList<EdgePassage> passages) => Write("passages.json", passages);

    private T? Read<T>(string name)
    {
        var path = Path.Combine(O.DataDirectory, name);
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) : default;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            logger.LogError(ex, "{File} could not be read", path);
            return default;
        }
    }

    private void Write<T>(string name, T value)
    {
        Directory.CreateDirectory(O.DataDirectory);
        var path = Path.Combine(O.DataDirectory, name);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Json));
        File.Move(temp, path, overwrite: true);
    }
}

/// <summary>Every second: decide quiet lanes. Every few seconds: is the cloud there; if so send what was decided here and keep the snapshot fresh.</summary>
public sealed class EdgeWorker(EdgeAgent agent, TimeProvider clock, ILogger<EdgeWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        agent.Load();
        DateTimeOffset? lastSnapshot = null;
        var lastCheck = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = clock.GetUtcNow();
            agent.Run(e =>
            {
                e?.Tick(now);
                return 0;
            });

            if (now - lastCheck >= TimeSpan.FromSeconds(5))
            {
                lastCheck = now;
                try
                {
                    await agent.SendAsync(stoppingToken);
                    if (lastSnapshot is null || now - lastSnapshot >= TimeSpan.FromMinutes(agent.O.SnapshotMinutes))
                    {
                        await agent.RefreshAsync(stoppingToken);
                        lastSnapshot = now;
                    }
                    agent.CloudAnswered();
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or JsonException && !stoppingToken.IsCancellationRequested)
                {
                    agent.CloudUnreachable(ex);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogError(ex, "Lane agent round failed");
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ContinueWith(_ => { }, CancellationToken.None);
        }
    }
}
