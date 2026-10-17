using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Yardsyncs.Application.Tos;
using Yardsyncs.Application.Tos.N4;
using Yardsyncs.Infrastructure.Persistence;

namespace Yardsyncs.Autogate.Tos;

/// <summary>
/// Gate passages written to Navis N4 through its gate web service (ArgoService genericInvoke with a &lt;gate&gt;
/// document): the truck visit and its transactions at the in-gate, the out-gate with N4's visit key. Used only for
/// terminals with tos_gate_write on. VERIFY the documents (<see cref="N4GateRequests"/>) on the customer's N4 test
/// system first.
/// </summary>
public sealed class N4GateWriter(YardsyncsDbContext db, IN4Client client, IOptionsMonitor<N4Options> options, ILogger<N4GateWriter> logger) : ITosGateWriter
{
    public async Task<TosGateResult> RecordAsync(int terminalId, TosGateRequest request, CancellationToken ct = default)
    {
        var code = await db.Terminals.AsNoTracking().Where(t => t.TerminalId == terminalId).Select(t => t.Code).SingleOrDefaultAsync(ct);
        var n4 = code is null ? null : options.CurrentValue.For(code);
        if (n4 is null)
            return TosGateResult.Unreachable($"N4 is not configured for terminal {code} (N4:Terminals:{code}).");

        var tranTypes = new Dictionary<string, string>(N4GateSetup.DefaultTranTypes, StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in n4.TranTypes)
            tranTypes[k] = v;
        var gate = new N4GateSetup(n4.GateId, n4.IngateStage, n4.OutgateStage, tranTypes);
        var doc = request.Stage == TosGateStage.GateIn ? N4GateRequests.GateIn(request, gate) : N4GateRequests.GateOut(request, gate);

        try
        {
            var result = N4GateRequests.Read(await client.InvokeAsync(n4, doc, ct));
            logger.LogInformation("N4 {Stage} for {No}: {Outcome} {Reference}", request.Stage, request.AppointmentNo, result.Ok ? "recorded" : "refused", result.VisitReference ?? result.Message);
            return result;
        }
        catch (TosUnavailableException ex)
        {
            return TosGateResult.Unreachable(ex.Message);
        }
    }
}
