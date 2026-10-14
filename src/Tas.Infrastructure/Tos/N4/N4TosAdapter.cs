using System.Text.Json;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tas.Application.Tos;
using Tas.Application.Tos.N4;
using Tas.Infrastructure.Persistence;

namespace Tas.Infrastructure.Tos.N4;

/// <summary>
/// Navis N4 as the TOS. Read-only: one <c>genericInvoke</c> call per check runs the TasLookup Groovy code
/// extension, which returns units, equipment orders (bookings / EDOs) and bills of lading as JSON; the rules in
/// <see cref="N4CheckEvaluator"/> turn that into checks. Nothing is written to N4.
/// </summary>
public sealed class N4TosAdapter(
    TasDbContext db,
    IN4Client client,
    IOptionsMonitor<N4Options> options,
    ILogger<N4TosAdapter> logger) : ITosAdapter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Source => "N4";

    public async Task<IReadOnlyList<TosCheck>> ValidateAsync(int terminalId, IReadOnlyList<TosItemQuery> items, CancellationToken ct = default)
    {
        var terminal = await db.Terminals.AsNoTracking()
            .Where(t => t.TerminalId == terminalId)
            .Select(t => new { t.Code, t.TimeZone })
            .SingleOrDefaultAsync(ct)
            ?? throw new TosUnavailableException($"Terminal {terminalId} not found.");

        var config = options.CurrentValue;
        var n4 = config.For(terminal.Code)
                 ?? throw new TosUnavailableException($"N4 is not configured for terminal {terminal.Code} (N4:Terminals:{terminal.Code}).");

        var evaluator = new N4CheckEvaluator(
            new N4HoldClassifier(config.HoldMapping),
            TimeZoneInfo.FindSystemTimeZoneById(terminal.TimeZone));

        var request = N4CheckEvaluator.RequestFor(items);
        if (request.Units.Count == 0 && request.Orders.Count == 0 && request.Bls.Count == 0)
            return evaluator.Evaluate(items, new N4LookupResult(Array.Empty<N4Unit>(), Array.Empty<N4Order>(), Array.Empty<N4BillOfLading>()));

        var started = DateTimeOffset.UtcNow;
        var payload = await client.InvokeAsync(n4, LookupDocument(n4.CodeExtension, request), ct);
        logger.LogDebug("N4 lookup for {Terminal}: {Units} unit(s), {Orders} order(s), {Bls} BL(s) in {Ms} ms",
            terminal.Code, request.Units.Count, request.Orders.Count, request.Bls.Count, (DateTimeOffset.UtcNow - started).TotalMilliseconds);

        return evaluator.Evaluate(items, ParsePayload(payload));
    }

    /// <summary>The genericInvoke document that runs the code extension with the request as JSON.</summary>
    public static string LookupDocument(string codeExtension, N4LookupRequest request) =>
        new XElement("groovy",
                new XAttribute("class-location", "code-extension"),
                new XAttribute("class-name", codeExtension),
                new XElement("parameters",
                    new XElement("parameter",
                        new XAttribute("id", "request"),
                        new XAttribute("value", JsonSerializer.Serialize(request, Json)))))
            .ToString(SaveOptions.DisableFormatting);

    /// <summary>The code extension's JSON answer; <c>{"error": "..."}</c> or anything unreadable means N4 couldn't answer.</summary>
    public static N4LookupResult ParsePayload(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.String)
                throw new TosUnavailableException($"The N4 lookup failed: {error.GetString()}");

            return doc.RootElement.Deserialize<N4LookupResult>(Json)
                   ?? throw new TosUnavailableException("N4 returned an empty lookup result.");
        }
        catch (JsonException ex)
        {
            throw new TosUnavailableException("N4 returned a lookup result TAS could not read.", ex);
        }
    }
}
