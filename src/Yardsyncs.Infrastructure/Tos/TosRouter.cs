using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Yardsyncs.Application.Tos;
using Yardsyncs.Application.Zone;
using Yardsyncs.Infrastructure.Persistence;
using Yardsyncs.Infrastructure.Tos.N4;
using Yardsyncs.Infrastructure.Zone;

namespace Yardsyncs.Infrastructure.Tos;

/// <summary>
/// The TOS adapter for each terminal (Terminals:&lt;code&gt;:Tos, default Tos:Adapter), so one installation can serve
/// a terminal on Navis N4 next to one in a demo or on another system. Every answer is held to <see cref="TosContract"/>:
/// one that breaks it counts as "the TOS could not be reached" (a warning and a re-check, never a pass).
/// </summary>
public sealed class TosRouter(IServiceProvider services, TerminalProfiles profiles, YardsyncsDbContext db, ILogger<TosRouter> logger) : ITosAdapter
{
    public string Source => Adapter(profiles.DefaultTos).Source;

    public string SourceFor(int terminalId) => Adapter(profiles.TosFor(TerminalProfiles.CachedCode(terminalId))).Source;

    public async Task<IReadOnlyList<TosCheck>> ValidateAsync(int terminalId, IReadOnlyList<TosItemQuery> items, CancellationToken ct = default) =>
        await (await ForAsync(terminalId, ct)).ValidateAsync(terminalId, items, ct);

    public async Task<IReadOnlyList<TosBlUnit>?> FindBlContainersAsync(int terminalId, string blNo, CancellationToken ct = default) =>
        await (await ForAsync(terminalId, ct)).FindBlContainersAsync(terminalId, blNo, ct);

    public async Task<IReadOnlyList<TosYardPosition>> YardPositionsAsync(int terminalId, IReadOnlyList<TosItemQuery> items, CancellationToken ct = default) =>
        await (await ForAsync(terminalId, ct)).YardPositionsAsync(terminalId, items, ct);

    /// <summary>The adapter name each terminal uses, e.g. "DEMO: Fake, JED1: N4".</summary>
    public string Summary(IEnumerable<string> terminalCodes)
    {
        var parts = terminalCodes.Select(code => $"{code}: {profiles.TosFor(code)}").ToList();
        return parts.Count == 0 ? profiles.DefaultTos : string.Join(", ", parts);
    }

    private async Task<ITosAdapter> ForAsync(int terminalId, CancellationToken ct)
    {
        var code = await TerminalProfiles.CodeAsync(db, terminalId, ct);
        ITosAdapter adapter = new TosContractGuard(Adapter(profiles.TosFor(code)), logger);
        // The zone system around the terminal (Nafith) adds its customs clearance to the TOS checks.
        return code is not null && services.GetService<IZoneSystem>() is { } zone && zone.CoversTerminal(code)
            ? new ZoneTosAdapter(adapter, zone, code, logger)
            : adapter;
    }

    private ITosAdapter Adapter(string name) => name switch
    {
        "N4" => services.GetRequiredService<N4TosAdapter>(),
        _ => services.GetRequiredService<FakeTosAdapter>(),
    };
}
