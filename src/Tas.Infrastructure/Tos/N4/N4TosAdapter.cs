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
        var (terminal, n4, config) = await ConnectionAsync(terminalId, ct);

        var evaluator = new N4CheckEvaluator(
            new N4HoldClassifier(config.HoldMapping),
            TimeZoneInfo.FindSystemTimeZoneById(terminal.TimeZone));

        var request = N4CheckEvaluator.RequestFor(items);
        if (request.Units.Count == 0 && request.Orders.Count == 0 && request.Bls.Count == 0)
            return evaluator.Evaluate(items, new N4LookupResult(Array.Empty<N4Unit>(), Array.Empty<N4Order>(), Array.Empty<N4BillOfLading>()));

        return evaluator.Evaluate(items, await LookupAsync(terminal.Code, n4, request, ct));
    }

    public async Task<IReadOnlyList<TosBlUnit>?> FindBlContainersAsync(int terminalId, string blNo, CancellationToken ct = default)
    {
        var (terminal, n4, _) = await ConnectionAsync(terminalId, ct);
        var nbr = blNo.Trim().ToUpperInvariant();
        var request = new N4LookupRequest(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), new[] { nbr });
        return BlContainers(await LookupAsync(terminal.Code, n4, request, ct), nbr);
    }

    public async Task<IReadOnlyList<TosYardPosition>> YardPositionsAsync(int terminalId, IReadOnlyList<TosItemQuery> items, CancellationToken ct = default)
    {
        var containers = items.Where(i => i.ContainerNo is not null).Select(i => i.ContainerNo!).Distinct().ToList();
        if (containers.Count == 0)
            return Array.Empty<TosYardPosition>();

        var (terminal, n4, _) = await ConnectionAsync(terminalId, ct);
        var result = await LookupAsync(terminal.Code, n4, new N4LookupRequest(containers, Array.Empty<string>(), Array.Empty<string>()), ct);
        return Positions(items, result);
    }

    /// <summary>Yard positions from the lookup answer: block and the rest of the slot ("A01.12.03.2" → A01, 12.03.2).</summary>
    public static IReadOnlyList<TosYardPosition> Positions(IReadOnlyList<TosItemQuery> items, N4LookupResult result)
    {
        var units = (result.Units ?? Array.Empty<N4Unit>()).Where(u => u.Found).ToDictionary(u => u.ContainerNo.ToUpperInvariant());
        var positions = new List<TosYardPosition>();
        foreach (var item in items)
        {
            if (item.ContainerNo is null || !units.TryGetValue(item.ContainerNo.ToUpperInvariant(), out var unit))
                continue;
            var slot = string.IsNullOrWhiteSpace(unit.Position) ? null : unit.Position.Trim();
            var block = string.IsNullOrWhiteSpace(unit.Block) ? null : unit.Block.Trim();
            if (slot is not null && slot.Contains('.'))
            {
                var parts = slot.Split('.', 2);
                block ??= parts[0];
                if (string.Equals(parts[0], block, StringComparison.OrdinalIgnoreCase))
                    slot = parts[1];
            }
            if (block is not null || slot is not null)
                positions.Add(new TosYardPosition(item.Index, block, slot, null));
        }
        return positions;
    }

    /// <summary>The containers of one BL from the lookup answer; null when N4 does not know the BL.</summary>
    public static IReadOnlyList<TosBlUnit>? BlContainers(N4LookupResult result, string nbr)
    {
        var bl = result.BlUnits?.FirstOrDefault(b => string.Equals(b.Nbr, nbr, StringComparison.OrdinalIgnoreCase));
        if (bl is null || !bl.Found)
            return null;
        return (bl.Units ?? Array.Empty<N4BlUnit>())
            .Where(u => !string.IsNullOrWhiteSpace(u.ContainerNo))
            .Select(u => new TosBlUnit(u.ContainerNo.Trim().ToUpperInvariant(), string.IsNullOrWhiteSpace(u.IsoType) ? null : u.IsoType.Trim().ToUpperInvariant()))
            .ToList();
    }

    private sealed record TerminalRef(string Code, string TimeZone);

    private async Task<(TerminalRef Terminal, N4TerminalOptions N4, N4Options Config)> ConnectionAsync(int terminalId, CancellationToken ct)
    {
        var terminal = await db.Terminals.AsNoTracking()
            .Where(t => t.TerminalId == terminalId)
            .Select(t => new TerminalRef(t.Code, t.TimeZone))
            .SingleOrDefaultAsync(ct)
            ?? throw new TosUnavailableException($"Terminal {terminalId} not found.");

        var config = options.CurrentValue;
        var n4 = config.For(terminal.Code)
                 ?? throw new TosUnavailableException($"N4 is not configured for terminal {terminal.Code} (N4:Terminals:{terminal.Code}).");
        return (terminal, n4, config);
    }

    private async Task<N4LookupResult> LookupAsync(string terminalCode, N4TerminalOptions n4, N4LookupRequest request, CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        var payload = await client.InvokeAsync(n4, LookupDocument(n4.CodeExtension, request), ct);
        logger.LogDebug("N4 lookup for {Terminal}: {Units} unit(s), {Orders} order(s), {Bls} BL(s), {BlUnits} BL unit list(s) in {Ms} ms",
            terminalCode, request.Units.Count, request.Orders.Count, request.Bls.Count, request.BlUnits?.Count ?? 0,
            (DateTimeOffset.UtcNow - started).TotalMilliseconds);
        return ParsePayload(payload);
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
