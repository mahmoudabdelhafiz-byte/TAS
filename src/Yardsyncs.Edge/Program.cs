using System.Text.Json;
using Yardsyncs.Application.Edge;
using Yardsyncs.Application.Gate;
using Yardsyncs.Edge;

// Yardsyncs lane agent: point the lane devices (or their vendor adapters) at this address instead of the cloud. The
// device API is the same; while the cloud answers, every call is passed through to it, and when it doesn't, the
// automatic lanes run here.

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<EdgeOptions>(builder.Configuration.GetSection("Edge"));
builder.Services.AddHttpClient("cloud");
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<EdgeAgent>();
builder.Services.AddHostedService<EdgeWorker>();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

var app = builder.Build();
var settings = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<EdgeOptions>>().Value;
if (string.IsNullOrWhiteSpace(settings.CloudUrl) || string.IsNullOrWhiteSpace(settings.ApiKey) || settings.TerminalId <= 0)
    throw new InvalidOperationException("Set Edge__CloudUrl, Edge__ApiKey (a gate system key of the terminal) and Edge__TerminalId.");

// How the agent is doing: for the terminal's monitoring.
app.MapGet("/edge/status", (EdgeAgent agent) => Results.Ok(agent.Run(e => new
{
    online = agent.Online,
    agent.LastOnlineAt,
    snapshotTakenAt = e?.Snapshot.TakenAt,
    snapshotValidUntil = e?.Snapshot.ValidUntil,
    toSend = e?.Passages.Count ?? 0,
})));

app.Map("/api/devices/{**rest}", async (HttpContext http, string? rest, EdgeAgent agent, TimeProvider clock) =>
{
    var path = rest ?? "";
    var body = await ReadBodyAsync(http.Request);
    var localCommand = TryLocalCommandId(path, out var commandId) && commandId < 0;

    if (agent.Online && !localCommand)
    {
        try
        {
            using var request = new HttpRequestMessage(new HttpMethod(http.Request.Method), $"api/devices/{path}{http.Request.QueryString}");
            if (http.Request.Headers.TryGetValue("X-Device-Key", out var key))
                request.Headers.TryAddWithoutValidation("X-Device-Key", key.ToString());
            if (body.Length > 0)
                request.Content = new ByteArrayContent(body) { Headers = { { "Content-Type", http.Request.ContentType ?? "application/json" } } };
            using var response = await agent.Cloud().SendAsync(request, http.RequestAborted);
            if ((int)response.StatusCode is not (502 or 503 or 504))
            {
                agent.CloudAnswered();
                var answer = await response.Content.ReadAsByteArrayAsync(http.RequestAborted);
                // Commands handed out here while the cloud was away and not yet confirmed still go to the device.
                if (http.Request.Method == "GET" && path == "commands" && response.IsSuccessStatusCode)
                {
                    var device = agent.Run(e => e?.Authenticate(http.Request.Headers["X-Device-Key"]));
                    IReadOnlyList<DeviceCommandView> local = device is null ? [] : agent.Run(e => e!.Pending(device, clock.GetUtcNow()));
                    if (local.Count > 0)
                    {
                        var cloud = JsonSerializer.Deserialize<List<DeviceCommandView>>(answer, EdgeAgent.Json) ?? [];
                        return Results.Json(local.Concat(cloud), EdgeAgent.Json);
                    }
                }
                http.Response.StatusCode = (int)response.StatusCode;
                if (response.Content.Headers.ContentType is { } type)
                    http.Response.ContentType = type.ToString();
                await http.Response.Body.WriteAsync(answer, http.RequestAborted);
                return Results.Empty;
            }
            agent.CloudUnreachable();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !http.RequestAborted.IsCancellationRequested)
        {
            agent.CloudUnreachable(ex);
        }
    }

    return Local(http, path, body, agent, clock.GetUtcNow());
});

app.Run();

// The device API answered here, from the snapshot.
static IResult Local(HttpContext http, string path, byte[] body, EdgeAgent agent, DateTimeOffset now)
{
    var device = agent.Run(e => e?.Authenticate(http.Request.Headers["X-Device-Key"]));
    if (agent.Run(e => e is null))
        return Results.Problem(statusCode: 503, detail: "The lane agent has no snapshot yet and the cloud can't be reached.");
    if (device is null)
        return Results.Problem(statusCode: 401, detail: "Send the device's key in X-Device-Key. The key is unknown, was replaced, or the device or its lane is no longer in use.");

    var method = http.Request.Method;
    if (method == "GET" && path == "me")
        return Results.Json(new { device.DeviceCode, device.LaneCode, device.Kind, agent.O.TerminalId, edge = true }, EdgeAgent.Json);
    if (method == "POST" && path == "status")
        return Results.Json(new { data = new { device.DeviceCode, edge = true } }, EdgeAgent.Json);
    if (method == "GET" && path == "commands")
        return Results.Json(agent.Run(e => e!.Pending(device, now)), EdgeAgent.Json);
    if (method == "POST" && path == "reads")
    {
        DeviceReadInput? input;
        try
        {
            input = JsonSerializer.Deserialize<DeviceReadInput>(body, EdgeAgent.Json);
        }
        catch (JsonException)
        {
            input = null;
        }
        if (input is null)
            return Results.Problem(statusCode: 400, detail: "The read is not valid JSON.");
        var (errors, read) = agent.Run(e => e!.OnRead(device, input, now));
        return read is null
            ? Results.Problem(statusCode: 400, detail: string.Join(" ", errors), extensions: new Dictionary<string, object?> { ["errors"] = errors })
            : Results.Json(new { data = read }, EdgeAgent.Json, statusCode: 201);
    }
    if (method == "POST" && TryLocalCommandId(path, out var commandId))
    {
        var ack = JsonSerializer.Deserialize<DeviceAckInput>(body.Length == 0 ? "{}"u8.ToArray() : body, EdgeAgent.Json) ?? new DeviceAckInput(true);
        return agent.Run(e => e!.Ack(device, commandId, ack.Done, now))
            ? Results.Json(new { data = new { commandId, ack.Done } }, EdgeAgent.Json)
            : Results.Problem(statusCode: 404, detail: $"Command {commandId} is not this device's.");
    }
    return Results.Problem(statusCode: 404, detail: "Not part of the device API.");
}

static bool TryLocalCommandId(string path, out long commandId)
{
    commandId = 0;
    var parts = path.Split('/');
    return parts is ["commands", var id, "ack"] && long.TryParse(id, out commandId);
}

static async Task<byte[]> ReadBodyAsync(HttpRequest request)
{
    using var buffer = new MemoryStream();
    await request.Body.CopyToAsync(buffer);
    return buffer.ToArray();
}
