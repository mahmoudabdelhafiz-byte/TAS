using Tas.Domain.Booking;

namespace Tas.Application.Reports;

/// <summary>One slot as the KPI report sees it (capacity in moves).</summary>
public sealed record KpiSlot(DateTimeOffset StartAt, int Capacity, int Booked, bool IsClosed);

/// <summary>One appointment as the KPI report sees it.</summary>
public sealed record KpiVisit(
    DateTimeOffset SlotStart,
    DateTimeOffset SlotEnd,
    AppointmentStatus Status,
    DateTimeOffset? ArrivedAt,
    DateTimeOffset? GateInAt,
    DateTimeOffset? GateOutAt,
    bool WalkIn);

/// <param name="Utilization">Booked ÷ capacity (0–1+), null without capacity.</param>
/// <param name="Appointments">Appointments not cancelled.</param>
/// <param name="AvgTurnMinutes">Arrival (or gate-in) to gate-out, completed visits.</param>
/// <param name="AvgWaitMinutes">Arrival to gate-in.</param>
public sealed record KpiDay(
    DateOnly Date, int Capacity, int Booked, double? Utilization, int Appointments, int Completed, int NoShows, int Cancelled,
    int WalkIns, int Arrived, int OnTime, double? AvgTurnMinutes, double? AvgWaitMinutes);

public sealed record KpiHour(int Hour, int Capacity, int Booked, double? Utilization, int Appointments, double? AvgTurnMinutes);

/// <param name="NoShowRate">No-shows ÷ appointments whose slot is over (not cancelled).</param>
/// <param name="OnTimeRate">Arrived inside the slot with the grace minutes ÷ arrived.</param>
/// <param name="WalkInShare">Walk-ins ÷ appointments.</param>
public sealed record KpiTotals(
    int Capacity, int Booked, double? Utilization, int Appointments, int Completed, int NoShows, int Cancelled, int WalkIns,
    double? NoShowRate, double? OnTimeRate, double? WalkInShare, double? AvgTurnMinutes, double? AvgWaitMinutes);

/// <param name="Yard">Times inside the terminal (driver app), when the terminal has yard stops.</param>
public sealed record KpiReport(DateOnly From, DateOnly To, KpiTotals Totals, IReadOnlyList<KpiDay> Days, IReadOnlyList<KpiHour> Hours, KpiYard? Yard = null);

/// <summary>Terminal KPIs for a date range (local dates of the slots): slot use, no-shows, punctuality, walk-ins, turn time.</summary>
public static class KpiCalculator
{
    public const int MaxDays = 92;

    public static KpiReport Compute(
        DateOnly from, DateOnly to, IReadOnlyList<KpiSlot> slots, IReadOnlyList<KpiVisit> visits, TimeZoneInfo timeZone,
        int earlyGraceMinutes, int lateGraceMinutes, DateTimeOffset now)
    {
        DateTimeOffset Local(DateTimeOffset t) => TimeZoneInfo.ConvertTime(t, timeZone);
        DateOnly DayOf(DateTimeOffset t) => DateOnly.FromDateTime(Local(t).DateTime);

        var open = slots.Where(s => !s.IsClosed).ToList();
        var days = new List<KpiDay>();
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            var date = d;
            var daySlots = open.Where(s => DayOf(s.StartAt) == date).ToList();
            var dayVisits = visits.Where(v => DayOf(v.SlotStart) == date).ToList();
            var m = Measure(dayVisits, earlyGraceMinutes, lateGraceMinutes, now);
            var capacity = daySlots.Sum(s => s.Capacity);
            var booked = daySlots.Sum(s => s.Booked);
            days.Add(new KpiDay(date, capacity, booked, Ratio(booked, capacity), m.Appointments, m.Completed, m.NoShows, m.Cancelled,
                m.WalkIns, m.Arrived, m.OnTime, m.AvgTurn, m.AvgWait));
        }

        var hours = Enumerable.Range(0, 24)
            .Select(h =>
            {
                var hourSlots = open.Where(s => Local(s.StartAt).Hour == h).ToList();
                var hourVisits = visits.Where(v => Local(v.SlotStart).Hour == h).ToList();
                var m = Measure(hourVisits, earlyGraceMinutes, lateGraceMinutes, now);
                var capacity = hourSlots.Sum(s => s.Capacity);
                var booked = hourSlots.Sum(s => s.Booked);
                return new KpiHour(h, capacity, booked, Ratio(booked, capacity), m.Appointments, m.AvgTurn);
            })
            .Where(h => h.Capacity > 0 || h.Appointments > 0)
            .ToList();

        var all = Measure(visits, earlyGraceMinutes, lateGraceMinutes, now);
        var totalCapacity = open.Sum(s => s.Capacity);
        var totalBooked = open.Sum(s => s.Booked);
        var totals = new KpiTotals(
            totalCapacity, totalBooked, Ratio(totalBooked, totalCapacity), all.Appointments, all.Completed, all.NoShows, all.Cancelled,
            all.WalkIns, Ratio(all.NoShows, all.Due), Ratio(all.OnTime, all.Arrived), Ratio(all.WalkIns, all.Appointments), all.AvgTurn, all.AvgWait);

        return new KpiReport(from, to, totals, days, hours);
    }

    private sealed record Measures(
        int Appointments, int Completed, int NoShows, int Cancelled, int WalkIns, int Arrived, int OnTime, int Due, double? AvgTurn, double? AvgWait);

    private static Measures Measure(IReadOnlyList<KpiVisit> visits, int early, int late, DateTimeOffset now)
    {
        var live = visits.Where(v => v.Status != AppointmentStatus.Cancelled).ToList();
        var arrived = live.Where(v => v.ArrivedAt is not null || v.GateInAt is not null).ToList();
        var onTime = arrived.Count(v =>
        {
            var at = v.ArrivedAt ?? v.GateInAt!.Value;
            return at >= v.SlotStart.AddMinutes(-early) && at <= v.SlotEnd.AddMinutes(late);
        });

        var turns = live
            .Where(v => v.GateOutAt is not null && (v.ArrivedAt ?? v.GateInAt) is not null)
            .Select(v => (v.GateOutAt!.Value - (v.ArrivedAt ?? v.GateInAt)!.Value).TotalMinutes)
            .Where(m => m >= 0)
            .ToList();
        var waits = live
            .Where(v => v.ArrivedAt is not null && v.GateInAt is not null)
            .Select(v => (v.GateInAt!.Value - v.ArrivedAt!.Value).TotalMinutes)
            .Where(m => m >= 0)
            .ToList();

        // An appointment counts for the no-show rate once its slot (plus grace) is over or the truck came.
        var due = live.Count(v => v.SlotEnd.AddMinutes(late) <= now || v.ArrivedAt is not null || v.GateInAt is not null
                                  || v.Status is AppointmentStatus.NoShow or AppointmentStatus.Completed);

        return new Measures(
            live.Count,
            live.Count(v => v.Status == AppointmentStatus.Completed),
            live.Count(v => v.Status == AppointmentStatus.NoShow),
            visits.Count(v => v.Status == AppointmentStatus.Cancelled),
            live.Count(v => v.WalkIn),
            arrived.Count,
            onTime,
            due,
            turns.Count > 0 ? Math.Round(turns.Average(), 1) : null,
            waits.Count > 0 ? Math.Round(waits.Average(), 1) : null);
    }

    private static double? Ratio(int part, int whole) => whole > 0 ? Math.Round((double)part / whole, 4) : null;
}
