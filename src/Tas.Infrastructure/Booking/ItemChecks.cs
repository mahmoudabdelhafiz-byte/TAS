using Tas.Application.Booking;
using Tas.Application.Tos;
using Tas.Domain;
using Tas.Domain.Booking;

namespace Tas.Infrastructure.Booking;

/// <summary>The result of one check on one item (Index = position of the item in the appointment).</summary>
public sealed record ItemCheck(int Index, CheckType Type, CheckResult Result, string? Message, string Source, string? TosRef);

public sealed record ItemToCheck(
    int Index, string CategoryCode, CategoryMode Mode, string? ContainerNo, string? BookingRef, string? EdoRef, string? CargoRef);

/// <summary>The checks run for every item — at booking and again before the truck arrives.</summary>
public static class ItemChecks
{
    public const string InternalSource = "INTERNAL";

    public static async Task<List<ItemCheck>> RunAsync(
        ITosAdapter tos, int terminalId, IReadOnlyList<ItemToCheck> items, DateTimeOffset slotStart, CancellationToken ct)
    {
        var checks = new List<ItemCheck>();

        foreach (var item in items.Where(i => i.ContainerNo is not null))
        {
            var valid = ContainerNumber.IsValid(item.ContainerNo!);
            checks.Add(new ItemCheck(item.Index, CheckType.ContainerNumber, valid ? CheckResult.Pass : CheckResult.Fail,
                valid ? null : $"{item.ContainerNo} has a wrong check digit — please re-check the number", InternalSource, null));
        }

        var tosChecks = await tos.ValidateAsync(
            terminalId,
            items.Select(i => new TosItemQuery(i.Index, i.CategoryCode, i.Mode, i.ContainerNo, i.BookingRef, i.EdoRef, i.CargoRef, slotStart)).ToList(),
            ct);
        checks.AddRange(tosChecks.Select(c => new ItemCheck(c.ItemIndex, c.Type, c.Result, c.Message, tos.Source, c.TosRef)));

        return checks;
    }
}
