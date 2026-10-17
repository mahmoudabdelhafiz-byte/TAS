using Yardsyncs.Application.Reports;
using Yardsyncs.Domain.Booking;

namespace Yardsyncs.Application.Tests;

public class CompanyReportTests
{
    private static readonly TimeZoneInfo Riyadh = TimeZoneInfo.FindSystemTimeZoneById("Asia/Riyadh");
    private static readonly DateOnly Day = new(2026, 10, 20);
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.FromHours(3));

    private static CompanyVisit Visit(int hour, AppointmentStatus status, string? plate, int? driverId, string? driver, int? arrived = null, int? outAt = null)
    {
        var start = new DateTimeOffset(Day.ToDateTime(new TimeOnly(hour, 0)), TimeSpan.FromHours(3));
        return new CompanyVisit(new KpiVisit(start, start.AddHours(1), status,
            arrived is null ? null : start.AddMinutes(arrived.Value), arrived is null ? null : start.AddMinutes(arrived.Value + 5),
            outAt is null ? null : start.AddMinutes(outAt.Value), false), plate, driverId, driver);
    }

    [Fact]
    public void Trucks_and_drivers_get_their_own_rows_busiest_first()
    {
        var visits = new[]
        {
            Visit(8, AppointmentStatus.Completed, "1234 ABJ", 1, "Abdullah", arrived: 5, outAt: 65),
            Visit(9, AppointmentStatus.NoShow, "1234 ABJ", 1, "Abdullah"),
            Visit(10, AppointmentStatus.Completed, "1234 ABJ", 2, "Mohammed", arrived: 90, outAt: 130), // late
            Visit(11, AppointmentStatus.Completed, "5521 KSA", 2, "Mohammed", arrived: 0, outAt: 30),
            Visit(12, AppointmentStatus.Cancelled, null, null, null),
        };

        var (totals, days, trucks, drivers) = CompanyReportCalculator.Compute(Day, Day.AddDays(1), visits, Riyadh, 30, 15, Now);

        Assert.Equal((4, 3, 1, 1), (totals.Appointments, totals.Completed, totals.NoShows, totals.Cancelled));
        Assert.Equal(0.25, totals.NoShowRate);
        Assert.Equal(2, days.Count);
        Assert.Equal(4, days[0].Appointments);
        Assert.Equal(0, days[0].Capacity);

        Assert.Equal(new[] { "1234 ABJ", "5521 KSA" }, trucks.Select(t => t.Name));
        var busy = trucks[0];
        Assert.Equal((3, 1), (busy.Appointments, busy.NoShows));
        Assert.Equal(0.5, busy.OnTimeRate); // one of two arrivals inside the slot and its grace
        Assert.Equal(50, busy.AvgTurnMinutes); // (60 + 40) / 2

        Assert.Equal(new[] { ("1", "Abdullah", 2), ("2", "Mohammed", 2) }, drivers.Select(d => (d.Key, d.Name, d.Appointments)));
    }
}
