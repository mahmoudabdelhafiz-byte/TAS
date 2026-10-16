using Microsoft.Extensions.Logging;
using Tas.Application.Tos;

namespace Tas.Infrastructure.Tos;

/// <summary>
/// Holds every answer of an adapter to <see cref="TosContract"/>. Checks that break it count as "the TOS could not
/// be reached" (a warning and a re-check, never a pass), and so does an unusable BL list; yard positions that break
/// it are left out, as directions are a help, not a decision. Every breach is logged as an error with the adapter.
/// </summary>
public sealed class TosContractGuard(ITosAdapter inner, ILogger logger) : ITosAdapter
{
    public string Source => inner.Source;

    public string SourceFor(int terminalId) => inner.SourceFor(terminalId);

    public async Task<IReadOnlyList<TosCheck>> ValidateAsync(int terminalId, IReadOnlyList<TosItemQuery> items, CancellationToken ct = default)
    {
        var answer = await inner.ValidateAsync(terminalId, items, ct);
        var problems = TosContract.CheckAnswer(items, answer);
        if (problems.Count == 0)
            return answer;
        logger.LogError("The {Source} adapter broke the TOS contract for terminal {TerminalId}: {Problems}", inner.Source, terminalId, string.Join("; ", problems));
        throw new TosUnavailableException($"The terminal system's answer was incomplete ({problems[0]}).");
    }

    public async Task<IReadOnlyList<TosBlUnit>?> FindBlContainersAsync(int terminalId, string blNo, CancellationToken ct = default)
    {
        var answer = await inner.FindBlContainersAsync(terminalId, blNo, ct);
        var problems = TosContract.CheckBlAnswer(answer);
        if (problems.Count == 0)
            return answer;
        logger.LogError("The {Source} adapter broke the TOS contract for BL {Bl}: {Problems}", inner.Source, blNo, string.Join("; ", problems));
        throw new TosUnavailableException($"The terminal system's answer for {blNo} could not be used ({problems[0]}).");
    }

    public async Task<IReadOnlyList<TosYardPosition>> YardPositionsAsync(int terminalId, IReadOnlyList<TosItemQuery> items, CancellationToken ct = default)
    {
        var answer = await inner.YardPositionsAsync(terminalId, items, ct) ?? [];
        if (TosContract.CheckPositions(items, answer) is not { Count: > 0 } problems)
            return answer;
        logger.LogError("The {Source} adapter broke the TOS contract for yard positions: {Problems}", inner.Source, string.Join("; ", problems));
        return answer
            .Where(p => TosContract.CheckPositions(items, [p]).Count == 0)
            .GroupBy(p => p.ItemIndex).Select(g => g.First()).ToList();
    }
}
