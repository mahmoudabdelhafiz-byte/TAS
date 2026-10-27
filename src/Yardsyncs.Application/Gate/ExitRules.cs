using Yardsyncs.Application.Booking;
using Yardsyncs.Domain;
using Yardsyncs.Domain.Gate;

namespace Yardsyncs.Application.Gate;

public enum ExitProblemKind
{
    /// <summary>The truck should leave with this container, but it wasn't seen on it.</summary>
    NotOnTruck,

    /// <summary>The truck brought this container to drop it, but it is still on it.</summary>
    StillOnTruck,

    /// <summary>A container is on the truck that this visit doesn't take out.</summary>
    NotBooked,
}

/// <summary>A move of the visit as the exit gate sees it.</summary>
/// <param name="ContainerNo">The container number, or a vehicle's VIN (RoRo).</param>
public sealed record ExitItem(string Category, CategoryMode Mode, CategoryFlow? Flow, string? ContainerNo);

public sealed record ExitProblem(ExitProblemKind Kind, string ContainerNo, string Message);

/// <param name="Seen">The containers seen on the truck (null: nothing was read).</param>
public sealed record ExitCheckResult(ExitCheckStatus Status, IReadOnlyList<ExitProblem> Problems, IReadOnlyList<string>? Seen);

/// <summary>The exit gate's check, free of the database.</summary>
public static class ExitRules
{
    /// <summary>
    /// The containers seen on the truck at the exit against the visit: each container taken out (flow Out) must be on
    /// the truck, each one brought in (flow In) must not be, and nothing else with a valid number may be on it.
    /// Moves without a direction, and cargo, are not checked. Nothing read: not checked.
    /// </summary>
    public static ExitCheckResult Check(IReadOnlyList<ExitItem> items, IReadOnlyCollection<string>? seen)
    {
        if (seen is null)
            return new ExitCheckResult(ExitCheckStatus.NotChecked, [], null);
        var onTruck = seen.Select(ContainerNumber.Normalize).Where(c => c.Length > 0).Distinct().ToList();
        var problems = new List<ExitProblem>();
        var containers = items.Where(i => i.Mode is CategoryMode.Container or CategoryMode.Vehicle && i.ContainerNo is not null).ToList();

        foreach (var item in containers.Where(i => i.Flow == CategoryFlow.Out && !onTruck.Contains(i.ContainerNo!)))
            problems.Add(new ExitProblem(ExitProblemKind.NotOnTruck, item.ContainerNo!,
                $"{item.ContainerNo} should leave on this truck but was not seen on it."));
        foreach (var item in containers.Where(i => i.Flow == CategoryFlow.In && onTruck.Contains(i.ContainerNo!)))
            problems.Add(new ExitProblem(ExitProblemKind.StillOnTruck, item.ContainerNo!,
                $"{item.ContainerNo} was brought to be dropped but is still on the truck."));
        var leaving = containers.Where(i => i.Flow is CategoryFlow.Out or null).Select(i => i.ContainerNo!).ToHashSet();
        var dropping = containers.Where(i => i.Flow == CategoryFlow.In).Select(i => i.ContainerNo!).ToHashSet();
        foreach (var c in onTruck.Where(c => (ContainerNumber.IsValid(c) || Vin.HasValidFormat(c)) && !leaving.Contains(c) && !dropping.Contains(c)))
            problems.Add(new ExitProblem(ExitProblemKind.NotBooked, c, $"{c} is on the truck but this visit doesn't take it out."));

        return new ExitCheckResult(problems.Count == 0 ? ExitCheckStatus.Ok : ExitCheckStatus.Problem, problems, onTruck);
    }

    /// <summary>The cargo's weight from the truck's weight in and out (whichever way it went), or null.</summary>
    public static decimal? NetKg(decimal? weightIn, decimal? weightOut) =>
        weightIn is { } i && weightOut is { } o && i > 0 && o > 0 ? Math.Abs(i - o) : null;
}
