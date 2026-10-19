using Yardsyncs.Application.Tos;
using Yardsyncs.Domain;
using Yardsyncs.Domain.Booking;

namespace Yardsyncs.Application.Zone;

// The zone system around the terminals: an economic zone's or port's truck platform (Nafith in Egypt's SCZone) that
// issues entry permits, verifies trucks and drivers, runs the marshalling yard and knows customs clearance (Nafeza).
// Yardsyncs asks it before booking and at the gate, tells it about bookings (pre-announcement) and hears from it when a
// truck enters or leaves the yard.

/// <summary>Customs clearance of one container as the zone system knows it.</summary>
public enum ZoneClearanceState
{
    Cleared,
    NotCleared,

    /// <summary>The zone system does not know the container (yet).</summary>
    Unknown,
}

/// <param name="Reference">The zone's or customs' reference (e.g. a Nafeza / ACI number), kept with the check.</param>
public sealed record ZoneClearance(string ContainerNo, ZoneClearanceState State, string? Message, string? Reference);

/// <summary>The truck and driver of a visit, as the zone system identifies them.</summary>
public sealed record ZoneTruck(string? Plate, string? DriverIdNumber, string? DriverMobile);

/// <param name="Valid">The truck and driver may enter the zone at that time.</param>
/// <param name="Problem">Why not, in the zone system's words (null when valid).</param>
public sealed record ZonePermit(bool Valid, string? PermitNo, DateTimeOffset? ValidUntil, string? Problem);

/// <summary>The zone system could not be reached or answered something unreadable.</summary>
public sealed class ZoneUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The zone system a terminal works with (configuration Zone:*, one adapter per system).</summary>
public interface IZoneSystem
{
    /// <summary>Name shown with its checks, e.g. "NAFITH".</summary>
    string Name { get; }

    /// <summary>Whether this terminal works with the zone system at all.</summary>
    bool CoversTerminal(string terminalCode);

    /// <summary>Customs clearance for each container (same order, one answer each).</summary>
    /// <exception cref="ZoneUnavailableException">The system could not be reached.</exception>
    Task<IReadOnlyList<ZoneClearance>> ClearanceAsync(string terminalCode, IReadOnlyList<string> containers, CancellationToken ct);

    /// <summary>The entry permit of a truck and driver for a visit at a time.</summary>
    /// <exception cref="ZoneUnavailableException">The system could not be reached.</exception>
    Task<ZonePermit> PermitAsync(string terminalCode, ZoneTruck truck, DateTimeOffset visitAt, CancellationToken ct);
}

/// <summary>What the zone system's answers mean for a booking and for the gate.</summary>
public static class ZoneRules
{
    public const string UnavailableMessage = "The zone system could not be reached — customs clearance will be re-checked before arrival";

    /// <summary>One customs check per container item, from the zone's clearance (Nafeza through the zone system).</summary>
    public static IReadOnlyList<TosCheck> ClearanceChecks(string system, IReadOnlyList<TosItemQuery> items, IReadOnlyList<ZoneClearance> answers)
    {
        var byContainer = answers
            .GroupBy(a => a.ContainerNo.Trim().ToUpperInvariant())
            .ToDictionary(g => g.Key, g => g.First());
        var checks = new List<TosCheck>();
        foreach (var item in items.Where(i => i.ContainerNo is not null && i.Mode == CategoryMode.Container))
        {
            var container = item.ContainerNo!.Trim().ToUpperInvariant();
            if (!byContainer.TryGetValue(container, out var answer))
                answer = new ZoneClearance(container, ZoneClearanceState.Unknown, null, null);
            checks.Add(answer.State switch
            {
                ZoneClearanceState.Cleared => new TosCheck(item.Index, CheckType.CustomsRelease, CheckResult.Pass,
                    null, Ref(system, answer.Reference)),
                ZoneClearanceState.NotCleared => new TosCheck(item.Index, CheckType.CustomsRelease, CheckResult.Fail,
                    $"{container}: not cleared by customs in {system}{(string.IsNullOrWhiteSpace(answer.Message) ? "" : $" ({Short(answer.Message, 150)})")}",
                    Ref(system, answer.Reference)),
                _ => new TosCheck(item.Index, CheckType.CustomsRelease, CheckResult.Warn,
                    $"{container}: {system} has no customs status yet — re-checked before arrival", null),
            });
        }
        return checks;
    }

    /// <summary>A warning per container item when the zone system can't be reached (the booking goes ahead; re-checked later).</summary>
    public static IReadOnlyList<TosCheck> UnavailableChecks(IReadOnlyList<TosItemQuery> items) =>
        items.Where(i => i.ContainerNo is not null && i.Mode == CategoryMode.Container)
            .Select(i => new TosCheck(i.Index, CheckType.CustomsRelease, CheckResult.Warn, UnavailableMessage, null))
            .ToList();

    /// <summary>The gate's line for a truck without a valid permit (null: the permit is fine).</summary>
    public static string? GateProblem(string system, string? plate, ZonePermit permit) =>
        permit.Valid ? null : $"No valid {system} entry permit for {plate ?? "this truck"}: {Short(permit.Problem ?? "not found", 150)}";

    private static string Short(string text, int max)
    {
        var value = text.Trim();
        return value.Length > max ? value[..max] : value;
    }

    private static string Ref(string system, string? reference)
    {
        var value = string.IsNullOrWhiteSpace(reference) ? system : $"{system}:{reference.Trim()}";
        return value.Length > 50 ? value[..50] : value; // appointment_item.tos_ref
    }
}
