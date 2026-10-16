namespace Tas.Application.Reports;

/// <summary>One stop of a visit inside the terminal (yard_instruction), with the visit's gate times.</summary>
public sealed record KpiStop(
    long AppointmentId, DateTimeOffset SlotStart, DateTimeOffset? GateInAt, DateTimeOffset? GateOutAt,
    int Sequence, string? Block, DateTimeOffset? ArrivedAt, DateTimeOffset? CompletedAt)
{
    /// <summary>The app saw the truck reach the block (a stop confirmed without that has ArrivedAt = CompletedAt).</summary>
    public bool ArrivalSeen => ArrivedAt is not null && (CompletedAt is null || CompletedAt > ArrivedAt);
}

/// <param name="AvgToBlockMinutes">Gate-in to arrival at the first block.</param>
/// <param name="AvgAtBlockMinutes">Arrival at a block to "Done here".</param>
public sealed record YardDay(DateOnly Date, int Visits, double? AvgToBlockMinutes, double? AvgAtBlockMinutes);

/// <param name="AvgDriveMinutes">From the gate or the previous stop to this block.</param>
public sealed record YardBlock(string Block, int Stops, double? AvgDriveMinutes, double? AvgAtBlockMinutes);

/// <param name="Visits">Visits with yard stops.</param>
/// <param name="AppSeenShare">Visits where the app saw the truck reach a block ÷ visits — how far the timings can be trusted.</param>
/// <param name="AvgToExitMinutes">Last stop done to gate-out.</param>
public sealed record KpiYard(
    int Visits, int Stops, double? AppSeenShare, double? AvgToBlockMinutes, double? AvgAtBlockMinutes, double? AvgToExitMinutes,
    IReadOnlyList<YardDay> Days, IReadOnlyList<YardBlock> Blocks);

/// <summary>Times inside the terminal from the driver app: gate to block, at the block, block to exit, per day and per block.</summary>
public static class YardKpiCalculator
{
    public static KpiYard Compute(DateOnly from, DateOnly to, IReadOnlyList<KpiStop> stops, TimeZoneInfo timeZone)
    {
        DateOnly DayOf(DateTimeOffset t) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(t, timeZone).DateTime);

        var visits = stops.Where(s => s.Block is not null).GroupBy(s => s.AppointmentId)
            .Select(g => g.OrderBy(s => s.Sequence).ToList())
            .ToList();

        static double? Avg(IEnumerable<double> values)
        {
            var list = values.Where(v => v >= 0).ToList();
            return list.Count == 0 ? null : Math.Round(list.Average(), 1);
        }

        static IEnumerable<double> ToBlock(IEnumerable<List<KpiStop>> v) => v
            .Select(list => list[0])
            .Where(s => s.ArrivalSeen && s.GateInAt is not null)
            .Select(s => (s.ArrivedAt!.Value - s.GateInAt!.Value).TotalMinutes);

        static IEnumerable<double> AtBlock(IEnumerable<KpiStop> s) => s
            .Where(x => x.ArrivalSeen && x.CompletedAt is not null)
            .Select(x => (x.CompletedAt!.Value - x.ArrivedAt!.Value).TotalMinutes);

        var days = new List<YardDay>();
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            var date = d;
            var dayVisits = visits.Where(v => DayOf(v[0].SlotStart) == date).ToList();
            days.Add(new YardDay(date, dayVisits.Count, Avg(ToBlock(dayVisits)), Avg(AtBlock(dayVisits.SelectMany(v => v)))));
        }

        // Per block: the drive there (from the gate or the previous stop) and the time spent.
        var legs = visits.SelectMany(v => v.Select((s, i) =>
        {
            var start = i == 0 ? s.GateInAt : v[i - 1].CompletedAt;
            double? drive = s.ArrivalSeen && start is not null ? (s.ArrivedAt!.Value - start.Value).TotalMinutes : null;
            return (Stop: s, Drive: drive);
        })).ToList();
        var blocks = legs
            .GroupBy(l => l.Stop.Block!.ToUpperInvariant())
            .Select(g => new YardBlock(g.Key, g.Count(), Avg(g.Where(l => l.Drive is not null).Select(l => l.Drive!.Value)), Avg(AtBlock(g.Select(l => l.Stop)))))
            .OrderByDescending(b => b.AvgAtBlockMinutes ?? -1)
            .ThenBy(b => b.Block, StringComparer.Ordinal)
            .ToList();

        var toExit = visits
            .Where(v => v[0].GateOutAt is not null && v.All(s => s.CompletedAt is not null))
            .Select(v => (v[0].GateOutAt!.Value - v.Max(s => s.CompletedAt!.Value)).TotalMinutes);

        return new KpiYard(
            visits.Count,
            visits.Sum(v => v.Count),
            visits.Count == 0 ? null : Math.Round((double)visits.Count(v => v.Any(s => s.ArrivalSeen)) / visits.Count, 4),
            Avg(ToBlock(visits)),
            Avg(AtBlock(visits.SelectMany(v => v))),
            Avg(toExit),
            days,
            blocks);
    }
}
