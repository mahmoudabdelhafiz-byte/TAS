using Microsoft.Extensions.Logging;
using Yardsyncs.Application.Tos;
using Yardsyncs.Application.Zone;
using Yardsyncs.Domain;

namespace Yardsyncs.Infrastructure.Zone;

/// <summary>
/// The terminal's TOS checks plus the zone system's customs clearance for each container (Nafeza through Nafith), at
/// booking, at every re-check and at the gate. A zone system that can't be reached gives a warning, never a pass:
/// the booking goes ahead and the container is re-checked before the truck comes.
/// </summary>
public sealed class ZoneTosAdapter(ITosAdapter inner, IZoneSystem zone, string terminalCode, ILogger logger) : ITosAdapter
{
    public string Source => inner.Source;

    public string SourceFor(int terminalId) => inner.SourceFor(terminalId);

    public async Task<IReadOnlyList<TosCheck>> ValidateAsync(int terminalId, IReadOnlyList<TosItemQuery> items, CancellationToken ct = default)
    {
        var checks = (await inner.ValidateAsync(terminalId, items, ct)).ToList();
        var containers = items.Where(i => i.ContainerNo is not null && i.Mode == CategoryMode.Container)
            .Select(i => Yardsyncs.Application.Booking.ContainerNumber.Normalize(i.ContainerNo!)).Distinct().ToList();
        if (containers.Count == 0)
            return checks;
        try
        {
            checks.AddRange(ZoneRules.ClearanceChecks(zone.Name, items, await zone.ClearanceAsync(terminalCode, containers, ct)));
        }
        catch (ZoneUnavailableException e)
        {
            logger.LogWarning(e, "{Zone} unreachable for terminal {Terminal}", zone.Name, terminalCode);
            checks.AddRange(ZoneRules.UnavailableChecks(items));
        }
        return checks;
    }

    public Task<IReadOnlyList<TosBlUnit>?> FindBlContainersAsync(int terminalId, string blNo, CancellationToken ct = default) =>
        inner.FindBlContainersAsync(terminalId, blNo, ct);

    public Task<IReadOnlyList<TosYardPosition>> YardPositionsAsync(int terminalId, IReadOnlyList<TosItemQuery> items, CancellationToken ct = default) =>
        inner.YardPositionsAsync(terminalId, items, ct);
}
