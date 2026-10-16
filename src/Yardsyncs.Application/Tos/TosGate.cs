namespace Yardsyncs.Application.Tos;

/// <summary>Which gate passage is recorded in the TOS.</summary>
public enum TosGateStage
{
    /// <summary>Create the truck visit and its transactions, and pass the in-gate.</summary>
    GateIn,

    /// <summary>Pass the out-gate and close the truck visit.</summary>
    GateOut,
}

/// <summary>One move of the visit as the TOS gate needs it.</summary>
/// <param name="CategoryCode">The Yardsyncs move type, e.g. IMP_PICKUP; the adapter maps it to the TOS's transaction type.</param>
/// <param name="Seals">Seal numbers seen at the gate (lane cameras or clerk).</param>
public sealed record TosGateMove(
    int Index,
    string CategoryCode,
    string Mode,
    string? ContainerNo,
    string? IsoType,
    string? BookingRef,
    string? EdoRef,
    string? CargoRef,
    IReadOnlyList<string> Seals);

/// <summary>A gate passage to record in the TOS.</summary>
/// <param name="VisitReference">The TOS's own key for the truck visit, from the gate-in record (gate-out only).</param>
public sealed record TosGateRequest(
    TosGateStage Stage,
    string AppointmentNo,
    string? TruckPlate,
    string? DriverName,
    string CompanyName,
    string? GateCode,
    string? LaneCode,
    DateTimeOffset At,
    string? VisitReference,
    IReadOnlyList<TosGateMove> Moves);

/// <summary>What the TOS said. <see cref="Retry"/> false means sending again won't help (refused, not unreachable).</summary>
public sealed record TosGateResult(bool Ok, string? VisitReference, string? Message, bool Retry = true)
{
    public static TosGateResult Done(string? visitReference, string? message = null) => new(true, visitReference, message, false);
    public static TosGateResult Refused(string message) => new(false, null, message, false);
    public static TosGateResult Unreachable(string message) => new(false, null, message, true);
}

/// <summary>
/// Records gate passages in the TOS: the truck visit with its transactions at gate-in, the out-gate at gate-out.
/// Optional for an adapter; Yardsyncs sends through it only for terminals that turn on tos_gate_write.
/// </summary>
public interface ITosGateWriter
{
    Task<TosGateResult> RecordAsync(int terminalId, TosGateRequest request, CancellationToken ct = default);
}

/// <summary>How gate records are retried when the TOS can't be reached.</summary>
public static class TosGateRetry
{
    /// <summary>After this many failed tries a record is given up and shown for a person to look at.</summary>
    public const int MaxAttempts = 10;

    /// <summary>1, 2, 4 … minutes, at most an hour.</summary>
    public static TimeSpan Delay(int attempts) => TimeSpan.FromMinutes(Math.Min(60, Math.Pow(2, Math.Max(0, attempts - 1))));
}
