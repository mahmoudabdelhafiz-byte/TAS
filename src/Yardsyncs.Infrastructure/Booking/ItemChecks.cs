using Yardsyncs.Application.Booking;
using Yardsyncs.Application.Tos;
using Yardsyncs.Domain;
using Yardsyncs.Domain.Booking;

namespace Yardsyncs.Infrastructure.Booking;

/// <summary>The result of one check on one item (Index = position of the item in the appointment).</summary>
public sealed record ItemCheck(int Index, CheckType Type, CheckResult Result, string? Message, string Source, string? TosRef);

public sealed record ItemToCheck(
    int Index, string CategoryCode, CategoryMode Mode, string? ContainerNo, string? BookingRef, string? EdoRef, string? CargoRef, string? Vin = null);

/// <summary>The checks run for every item — at booking and again before the truck arrives.</summary>
public static class ItemChecks
{
    public const string InternalSource = "INTERNAL";

    public const string TosUnavailableMessage = "The terminal system could not be reached — this will be re-checked before arrival";

    /// <param name="onUnavailable">
    /// What to do when the TOS can't be reached: null rethrows <see cref="TosUnavailableException"/> (re-checks, gate);
    /// Warn adds a TOS_UNAVAILABLE warning per item so the booking can go ahead; Reject rethrows too.
    /// </param>
    public static async Task<List<ItemCheck>> RunAsync(
        ITosAdapter tos, int terminalId, IReadOnlyList<ItemToCheck> items, DateTimeOffset slotStart, CancellationToken ct,
        TosUnavailablePolicy? onUnavailable = null)
    {
        var checks = new List<ItemCheck>();

        foreach (var item in items.Where(i => i.ContainerNo is not null))
        {
            var valid = ContainerNumber.IsValid(item.ContainerNo!);
            checks.Add(new ItemCheck(item.Index, CheckType.ContainerNumber, valid ? CheckResult.Pass : CheckResult.Fail,
                valid ? null : $"{item.ContainerNo} has a wrong check digit — please re-check the number", InternalSource, null));
        }

        IReadOnlyList<TosCheck> tosChecks;
        try
        {
            tosChecks = await tos.ValidateAsync(
                terminalId,
                items.Select(i => new TosItemQuery(i.Index, i.CategoryCode, i.Mode, i.ContainerNo, i.BookingRef, i.EdoRef, i.CargoRef, slotStart, i.Vin)).ToList(),
                ct);
        }
        catch (TosUnavailableException) when (onUnavailable == TosUnavailablePolicy.Warn)
        {
            checks.AddRange(items.Select(i => new ItemCheck(i.Index, CheckType.TosUnavailable, CheckResult.Warn, TosUnavailableMessage, tos.SourceFor(terminalId), null)));
            return checks;
        }

        checks.AddRange(tosChecks.Select(c => new ItemCheck(c.ItemIndex, c.Type, c.Result, c.Message, tos.SourceFor(terminalId), c.TosRef)));
        return checks;
    }
}
