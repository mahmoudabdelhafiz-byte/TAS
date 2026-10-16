using Tas.Domain;
using Tas.Domain.Booking;

namespace Tas.Application.Tos;

/// <summary>One move to check against the terminal operating system.</summary>
public sealed record TosItemQuery(
    int Index,
    string CategoryCode,
    CategoryMode Mode,
    string? ContainerNo,
    string? BookingRef,
    string? EdoRef,
    string? CargoRef,
    DateTimeOffset SlotStart);

/// <summary>The result of one TOS check for one item.</summary>
public sealed record TosCheck(int ItemIndex, CheckType Type, CheckResult Result, string? Message, string? TosRef = null);

/// <summary>
/// Where a move happens in the yard: the block and slot of an import / empty to pick up, or the block / transfer
/// point planned for a container being delivered. Any part may be unknown (null).
/// </summary>
/// <param name="Slot">Row, bay and tier inside the block as the TOS writes them, e.g. "14.03.2".</param>
public sealed record TosYardPosition(int ItemIndex, string? Block, string? Slot, string? TransferPoint);

/// <summary>A container listed on a bill of lading, with its ISO type when the TOS knows it (e.g. "22G1", "45G1").</summary>
public sealed record TosBlUnit(string ContainerNo, string? IsoType);

/// <summary>
/// The terminal operating system as the booking flow sees it. The first implementation is a fake;
/// a Navis N4 adapter (web services / EDI) replaces it per terminal.
/// </summary>
public interface ITosAdapter
{
    /// <summary>Name stored with each check result, e.g. "N4" or "FAKE_TOS".</summary>
    string Source { get; }

    /// <summary>The name for one terminal's checks, when the adapter serves terminals with different systems.</summary>
    string SourceFor(int terminalId) => Source;

    /// <summary>Availability, holds, releases, payment, booking validity, VGM and cut-off for each item.</summary>
    Task<IReadOnlyList<TosCheck>> ValidateAsync(int terminalId, IReadOnlyList<TosItemQuery> items, CancellationToken ct = default);

    /// <summary>The containers on an import bill of lading, or null when the TOS does not know the BL.</summary>
    Task<IReadOnlyList<TosBlUnit>?> FindBlContainersAsync(int terminalId, string blNo, CancellationToken ct = default);

    /// <summary>Yard positions for a visit's moves (for the driver's directions after gate-in).</summary>
    Task<IReadOnlyList<TosYardPosition>> YardPositionsAsync(int terminalId, IReadOnlyList<TosItemQuery> items, CancellationToken ct = default);
}
