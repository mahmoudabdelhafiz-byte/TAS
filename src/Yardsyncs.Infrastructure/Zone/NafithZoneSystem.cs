using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Yardsyncs.Application.Zone;

namespace Yardsyncs.Infrastructure.Zone;

/// <summary>
/// Configuration section "Nafith": the Nafith truck platform around the terminals (Egypt's SCZone). Mode Off (default),
/// Simulator (answers from the rules below, for demos and tests) or Live. The API key belongs in an environment
/// variable (Nafith__ApiKey). The paths and field names follow docs/nafith.md and are adjusted to Nafith's API when it
/// is shared; {container}, {plate}, {driverId}, {mobile} and {at} are filled in.
/// </summary>
public sealed class NafithOptions
{
    public string Mode { get; set; } = "Off";

    /// <summary>Terminal codes that work with Nafith; empty = every terminal of the installation.</summary>
    public List<string> Terminals { get; set; } = [];

    public string BaseUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string ApiKeyHeader { get; set; } = "X-Api-Key";
    public int TimeoutSeconds { get; set; } = 8;

    public string ClearancePath { get; set; } = "/api/v1/containers/{container}/clearance";
    public string PermitPath { get; set; } = "/api/v1/permits/check?plate={plate}&driverId={driverId}&at={at}";

    /// <summary>The key Nafith sends (header X-Zone-Key) when it tells Yardsyncs about trucks in its yard.</summary>
    public string InboundKey { get; set; } = "";

    /// <summary>Simulator: containers that are not cleared by customs (the rest are cleared).</summary>
    public List<string> SimulatorNotCleared { get; set; } = ["MSCU7654329"];

    /// <summary>Simulator: plates without a valid entry permit (the rest have one).</summary>
    public List<string> SimulatorNoPermit { get; set; } = ["9021KHB"];

    public bool IsOn => Mode.Equals("Live", StringComparison.OrdinalIgnoreCase) || IsSimulator;
    public bool IsSimulator => Mode.Equals("Simulator", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Nafith as the zone system: customs clearance per container (Nafeza through Nafith) and the entry permit of a
/// truck and driver. Live calls Nafith's API; Simulator answers from <see cref="NafithOptions"/> without a network.
/// </summary>
public sealed class NafithZoneSystem(HttpClient http, IOptions<NafithOptions> options) : IZoneSystem
{
    private readonly NafithOptions _o = options.Value;

    public string Name => "NAFITH";

    public bool CoversTerminal(string terminalCode) =>
        _o.IsOn && (_o.Terminals.Count == 0 || _o.Terminals.Contains(terminalCode, StringComparer.OrdinalIgnoreCase));

    public async Task<IReadOnlyList<ZoneClearance>> ClearanceAsync(string terminalCode, IReadOnlyList<string> containers, CancellationToken ct)
    {
        if (_o.IsSimulator)
            return containers.Select(c => _o.SimulatorNotCleared.Contains(c, StringComparer.OrdinalIgnoreCase)
                ? new ZoneClearance(c, ZoneClearanceState.NotCleared, "Customs inspection pending", "ACI-SIM-" + Tail(c))
                : new ZoneClearance(c, ZoneClearanceState.Cleared, null, "ACI-SIM-" + Tail(c))).ToList();

        var answers = new List<ZoneClearance>();
        foreach (var container in containers)
        {
            var json = await GetAsync(Fill(_o.ClearancePath, ("container", container)), ct);
            answers.Add(ReadClearance(container, json));
        }
        return answers;
    }

    public async Task<ZonePermit> PermitAsync(string terminalCode, ZoneTruck truck, DateTimeOffset visitAt, CancellationToken ct)
    {
        if (_o.IsSimulator)
        {
            var plate = (truck.Plate ?? "").Replace(" ", "");
            return _o.SimulatorNoPermit.Any(p => p.Replace(" ", "").Equals(plate, StringComparison.OrdinalIgnoreCase))
                ? new ZonePermit(false, null, null, "permit expired")
                : new ZonePermit(true, "NP-SIM-" + (plate.Length > 4 ? plate[^4..] : plate), visitAt.AddHours(12), null);
        }

        var json = await GetAsync(Fill(_o.PermitPath,
            ("plate", truck.Plate ?? ""), ("driverId", truck.DriverIdNumber ?? ""), ("mobile", truck.DriverMobile ?? ""),
            ("at", visitAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"))), ct);
        return ReadPermit(json);
    }

    // ------------------------------------------------------------------ reading Nafith's answers

    /// <summary>
    /// { "status": "CLEARED" | "NOT_CLEARED" | "UNKNOWN", "message": "…", "reference": "…" } — "cleared": true/false and
    /// "state" are read too, so small differences in Nafith's API don't break the check.
    /// </summary>
    public static ZoneClearance ReadClearance(string container, JsonNode? json)
    {
        if (json is not JsonObject)
            return new ZoneClearance(container, ZoneClearanceState.Unknown, null, null);
        var status = (Text(json, "status") ?? Text(json, "state") ?? "").Trim().ToUpperInvariant().Replace(' ', '_').Replace('-', '_');
        var cleared = json["cleared"] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : (bool?)null;
        var state = status switch
        {
            "CLEARED" or "RELEASED" or "OK" => ZoneClearanceState.Cleared,
            "NOT_CLEARED" or "HOLD" or "ON_HOLD" or "BLOCKED" or "PENDING" => ZoneClearanceState.NotCleared,
            _ when cleared is true => ZoneClearanceState.Cleared,
            _ when cleared is false => ZoneClearanceState.NotCleared,
            _ => ZoneClearanceState.Unknown,
        };
        return new ZoneClearance(container, state, Text(json, "message") ?? Text(json, "reason"), Text(json, "reference") ?? Text(json, "aciNumber"));
    }

    /// <summary>{ "valid": true, "permitNo": "…", "validUntil": "…", "reason": "…" }.</summary>
    public static ZonePermit ReadPermit(JsonNode? json)
    {
        if (json is not JsonObject)
            return new ZonePermit(false, null, null, "not found");
        var valid = json["valid"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
        DateTimeOffset? until = DateTimeOffset.TryParse(Text(json, "validUntil"), out var u) ? u : null;
        return new ZonePermit(valid, Text(json, "permitNo"), until, valid ? null : Text(json, "reason") ?? Text(json, "message") ?? "not valid");
    }

    private static string Tail(string value) => value.Length > 4 ? value[^4..] : value;

    private static string? Text(JsonNode json, string name) =>
        json[name] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;

    private static string Fill(string path, params (string Name, string Value)[] values) =>
        values.Aggregate(path, (p, v) => p.Replace("{" + v.Name + "}", Uri.EscapeDataString(v.Value)));

    /// <summary>GET and parse; 404 = not known (null). Anything else unreadable = the zone system is unavailable.</summary>
    private async Task<JsonNode?> GetAsync(string path, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_o.BaseUrl))
            throw new ZoneUnavailableException("Nafith__BaseUrl is not set.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_o.TimeoutSeconds, 1, 60)));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _o.BaseUrl.TrimEnd('/') + path);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (_o.ApiKey.Length > 0)
                request.Headers.TryAddWithoutValidation(_o.ApiKeyHeader, _o.ApiKey);
            using var response = await http.SendAsync(request, timeout.Token);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return null;
            if (!response.IsSuccessStatusCode)
                throw new ZoneUnavailableException($"Nafith answered {(int)response.StatusCode}.");
            return JsonNode.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
        {
            throw new ZoneUnavailableException("Nafith could not be reached.", e);
        }
    }
}
