using Yardsyncs.Domain.Gate;

namespace Yardsyncs.Application.Reports;

// Lane KPIs (Yardsyncs Autogate): how the lanes handle trucks — how fast, how often without a clerk, how well the
// cameras read. Computed from the lane controller's transactions (one per truck at a lane).

/// <summary>A lane as the KPIs see it: whether it has a camera reading plates or container numbers today.</summary>
public sealed record KpiLane(int LaneId, string LaneCode, LaneDirection Direction, LaneMode Mode, bool ReadsPlates, bool ReadsContainers);

/// <summary>One truck at a lane.</summary>
/// <param name="Reason">Why a clerk had to help (null: the lane decided alone).</param>
public sealed record KpiLaneTransaction(
    int LaneId, LaneTransactionStatus Status, DateTimeOffset StartedAt, DateTimeOffset? DecidedAt, DateTimeOffset? ClosedAt,
    bool PlateRead, bool PlateCorrected, bool ContainersRead, bool ContainersCorrected, string? Reason);

/// <param name="Trucks">Trucks the lane decided on (an abandoned transaction — nobody to identify — is not a truck).</param>
/// <param name="Abandoned">Reads the lane gave up on: nothing to identify a truck by.</param>
/// <param name="StraightThroughRate">Decided by the lane alone, no clerk ÷ trucks.</param>
/// <param name="ExceptionRate">A clerk had to help ÷ trucks.</param>
/// <param name="MedianDecisionSeconds">First read to the lane's decision.</param>
/// <param name="MedianLaneSeconds">First read to through the barrier (clerk help included).</param>
/// <param name="P90LaneSeconds">Nine trucks in ten were through the barrier within this.</param>
/// <param name="PlateReadRate">At lanes with a plate camera: plate read and not corrected by a clerk ÷ trucks.</param>
/// <param name="ContainerReadRate">At lanes with a container camera, trucks whose containers were read or corrected: read right ÷ those.</param>
public sealed record LaneKpis(
    int Trucks, int Abandoned, double? StraightThroughRate, double? ExceptionRate,
    double? MedianDecisionSeconds, double? MedianLaneSeconds, double? P90LaneSeconds,
    double? PlateReadRate, double? ContainerReadRate);

public sealed record LaneKpiRow(int LaneId, string LaneCode, LaneDirection Direction, LaneMode Mode, LaneKpis Kpis);

public sealed record LaneKpiDay(DateOnly Date, int Trucks, double? ExceptionRate, double? MedianLaneSeconds);

/// <summary>Why clerks had to help, most frequent first.</summary>
public sealed record LaneKpiReason(string Reason, int Count, double Share);

public sealed record LaneKpiReport(
    DateOnly From, DateOnly To, LaneKpis Totals, IReadOnlyList<LaneKpiRow> Lanes, IReadOnlyList<LaneKpiDay> Days, IReadOnlyList<LaneKpiReason> Reasons);

public static class LaneKpiCalculator
{
    public static LaneKpiReport Compute(
        DateOnly from, DateOnly to, IReadOnlyList<KpiLane> lanes, IReadOnlyList<KpiLaneTransaction> transactions, TimeZoneInfo timeZone)
    {
        DateOnly DayOf(DateTimeOffset t) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(t, timeZone).DateTime);

        var byLane = lanes.ToDictionary(l => l.LaneId);
        var inRange = transactions
            .Where(t => t.Status != LaneTransactionStatus.Open && byLane.ContainsKey(t.LaneId))
            .Where(t => DayOf(t.StartedAt) is var d && d >= from && d <= to)
            .ToList();

        var rows = lanes
            .OrderBy(l => l.Direction).ThenBy(l => l.LaneCode, StringComparer.Ordinal)
            .Select(l => new LaneKpiRow(l.LaneId, l.LaneCode, l.Direction, l.Mode, Kpis(inRange.Where(t => t.LaneId == l.LaneId).ToList(), byLane)))
            .Where(r => r.Kpis.Trucks + r.Kpis.Abandoned > 0 || byLane[r.LaneId].Mode == LaneMode.Automatic)
            .ToList();

        var days = new List<LaneKpiDay>();
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            var date = d;
            var k = Kpis(inRange.Where(t => DayOf(t.StartedAt) == date).ToList(), byLane);
            days.Add(new LaneKpiDay(date, k.Trucks, k.ExceptionRate, k.MedianLaneSeconds));
        }

        var helped = inRange.Where(IsTruck).Where(t => t.Reason is not null).ToList();
        var reasons = helped.GroupBy(t => t.Reason!)
            .Select(g => new LaneKpiReason(g.Key, g.Count(), Math.Round((double)g.Count() / helped.Count, 3)))
            .OrderByDescending(r => r.Count).ThenBy(r => r.Reason, StringComparer.Ordinal)
            .ToList();

        return new LaneKpiReport(from, to, Kpis(inRange, byLane), rows, days, reasons);
    }

    private static bool IsTruck(KpiLaneTransaction t) => t.Status is not (LaneTransactionStatus.Open or LaneTransactionStatus.Abandoned);

    private static LaneKpis Kpis(IReadOnlyList<KpiLaneTransaction> all, IReadOnlyDictionary<int, KpiLane> lanes)
    {
        var trucks = all.Where(IsTruck).ToList();
        var abandoned = all.Count(t => t.Status == LaneTransactionStatus.Abandoned);

        var decision = trucks.Where(t => t.DecidedAt is not null).Select(t => (t.DecidedAt!.Value - t.StartedAt).TotalSeconds).Where(s => s >= 0).ToList();
        var atLane = trucks.Where(t => t.Status == LaneTransactionStatus.Passed && t.ClosedAt is not null)
            .Select(t => (t.ClosedAt!.Value - t.StartedAt).TotalSeconds).Where(s => s >= 0).ToList();

        var plates = trucks.Where(t => lanes[t.LaneId].ReadsPlates).ToList();
        var containers = trucks.Where(t => lanes[t.LaneId].ReadsContainers && (t.ContainersRead || t.ContainersCorrected)).ToList();

        return new LaneKpis(
            trucks.Count,
            abandoned,
            Share(trucks.Count(t => t.Reason is null), trucks.Count),
            Share(trucks.Count(t => t.Reason is not null), trucks.Count),
            Percentile(decision, 0.5),
            Percentile(atLane, 0.5),
            Percentile(atLane, 0.9),
            Share(plates.Count(t => t.PlateRead && !t.PlateCorrected), plates.Count),
            Share(containers.Count(t => t.ContainersRead && !t.ContainersCorrected), containers.Count));
    }

    private static double? Share(int part, int whole) => whole == 0 ? null : Math.Round((double)part / whole, 3);

    /// <summary>The value below which this share of the values lie (nearest rank), in whole seconds.</summary>
    public static double? Percentile(IReadOnlyList<double> values, double p)
    {
        if (values.Count == 0) return null;
        var sorted = values.OrderBy(v => v).ToList();
        var rank = (int)Math.Ceiling(p * sorted.Count);
        return Math.Round(sorted[Math.Clamp(rank, 1, sorted.Count) - 1]);
    }
}
