using Tas.Application.Capacity;
using Tas.Application.Gate;
using Tas.Domain;
using Tas.Domain.Booking;
using Tas.Domain.Capacity;
using Tas.Domain.Gate;

namespace Tas.Application.Tests;

public class GateMatcherTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 10, 5, 0, TimeSpan.FromHours(3));

    private static GateCandidate Candidate(long id, int startHour, string? plate = "1234ABJ", AppointmentStatus status = AppointmentStatus.Confirmed, string[]? containers = null) =>
        new(id, $"DEMO-261020-{id:00000}", $"QR{id}", status,
            new DateTimeOffset(2026, 10, 20, startHour, 0, 0, TimeSpan.FromHours(3)),
            new DateTimeOffset(2026, 10, 20, startHour + 1, 0, 0, TimeSpan.FromHours(3)),
            plate, (containers ?? Array.Empty<string>()).ToHashSet());

    private static GateReads Reads(string? plate = null, string[]? containers = null, string? qr = null, string? number = null) =>
        new(plate, containers ?? [], qr, number);

    [Fact]
    public void Qr_code_wins_over_everything_else()
    {
        var match = GateMatcher.Match(Reads(plate: "5678 HDR", qr: "qr2"), [Candidate(1, 10), Candidate(2, 14, "5678HDR")], Now);

        Assert.Equal(2, match.Appointment!.AppointmentId);
        Assert.Equal(MatchMethod.QrCode, match.Method);
    }

    [Fact]
    public void Arabic_plate_read_picks_the_truck_appointment_nearest_to_now()
    {
        var match = GateMatcher.Match(Reads(plate: "أ ب ح ١٢٣٤"), [Candidate(1, 6), Candidate(2, 10), Candidate(3, 15)], Now);

        Assert.Equal(2, match.Appointment!.AppointmentId);
        Assert.Equal(MatchMethod.Plate, match.Method);
        Assert.Empty(match.Warnings);
    }

    [Fact]
    public void Live_appointments_are_preferred_over_finished_ones()
    {
        var match = GateMatcher.Match(Reads(plate: "1234 ABJ"),
            [Candidate(1, 10, status: AppointmentStatus.Completed), Candidate(2, 13)], Now);

        Assert.Equal(2, match.Appointment!.AppointmentId);
    }

    [Fact]
    public void Container_read_finds_the_appointment_when_the_plate_is_unknown()
    {
        var match = GateMatcher.Match(Reads(plate: "9999 ZZZ", containers: ["hlxu 870001-8"]),
            [Candidate(1, 10, containers: ["MSCU1234566"]), Candidate(2, 10, plate: null, containers: ["HLXU8700018"])], Now);

        Assert.Equal(2, match.Appointment!.AppointmentId);
        Assert.Equal(MatchMethod.Container, match.Method);
    }

    [Fact]
    public void Reads_that_do_not_fit_the_appointment_are_warned_about()
    {
        var match = GateMatcher.Match(Reads(plate: "1234 ABJ", containers: ["TGHU3000017"]), [Candidate(1, 10, containers: ["HLXU8700018"])], Now);

        Assert.Equal(1, match.Appointment!.AppointmentId);
        Assert.Contains("TGHU3000017", Assert.Single(match.Warnings));
    }

    [Fact]
    public void Nothing_matching_gives_no_appointment()
    {
        var match = GateMatcher.Match(Reads(plate: "9999 ZZZ", qr: "nope"), [Candidate(1, 10)], Now);

        Assert.Null(match.Appointment);
        Assert.Equal(MatchMethod.None, match.Method);
        Assert.Contains(match.Warnings, w => w.Contains("QR code"));
    }
}

public class GateDeciderTests
{
    private static readonly TimeZoneInfo Riyadh = TimeZoneInfo.FindSystemTimeZoneById("Asia/Riyadh");
    private static readonly TerminalSettings Settings = TerminalSettings.Defaults; // 30 min early / late grace
    private static DateTimeOffset At(int hour, int minute = 0) => new(2026, 10, 20, hour, minute, 0, TimeSpan.FromHours(3));

    private static GateCandidate Appointment(AppointmentStatus status = AppointmentStatus.EnRoute) =>
        new(1, "DEMO-261020-AAAAA", "QR", status, At(10), At(11), "1234ABJ", new HashSet<string>());

    private static GateDecision Decide(GateCandidate? a, DateTimeOffset now, TrafficLight light = TrafficLight.Green, string reason = "",
        bool truckBlocked = false, bool driverBlocked = false) =>
        GateDecider.Decide(a, light, reason, truckBlocked, driverBlocked, now, Settings, Riyadh);

    [Fact]
    public void Clear_truck_inside_its_window_is_admitted()
    {
        Assert.Equal(GateDecisionKind.Admit, Decide(Appointment(), At(9, 30)).Kind);
        Assert.Equal(GateDecisionKind.Admit, Decide(Appointment(), At(11, 30)).Kind);
        var amber = Decide(Appointment(), At(10, 15), TrafficLight.Amber, "Storage charges start today");
        Assert.Equal(GateDecisionKind.Admit, amber.Kind);
        Assert.Contains("Storage charges", amber.Message);
    }

    [Fact]
    public void Early_and_late_trucks_go_to_the_trouble_desk_with_local_times()
    {
        var early = Decide(Appointment(), At(9, 0), TrafficLight.Grey);
        Assert.Equal(TroubleReason.Early, early.Reason);
        Assert.Contains("09:30", early.Message);

        var late = Decide(Appointment(), At(11, 31), TrafficLight.Red, "Arrival window has passed");
        Assert.Equal(TroubleReason.Late, late.Reason);
        Assert.Contains("11:30", late.Message);
    }

    [Fact]
    public void Red_checks_blocked_vehicles_and_wrong_statuses_are_referred()
    {
        Assert.Equal(TroubleReason.NotCleared, Decide(Appointment(), At(10), TrafficLight.Red, "Customs hold").Reason);
        Assert.Equal(TroubleReason.Blocked, Decide(Appointment(), At(10), truckBlocked: true).Reason);
        Assert.Equal(TroubleReason.Blocked, Decide(Appointment(), At(10), driverBlocked: true).Reason);
        Assert.Equal(TroubleReason.AlreadyIn, Decide(Appointment(AppointmentStatus.GateIn), At(10)).Reason);
        Assert.Equal(TroubleReason.Cancelled, Decide(Appointment(AppointmentStatus.Cancelled), At(10)).Reason);
        Assert.Equal(TroubleReason.NoAppointment, Decide(null, At(10)).Reason);
    }

    [Fact]
    public void Walk_ins_may_use_the_walk_in_reserve_but_not_company_reservations()
    {
        var slot = At(10);
        CapacityReservation[] reservations =
        [
            new() { CompanyId = null, StartAt = slot.AddDays(-1), EndAt = slot.AddDays(1), UnitsPerSlot = 2, ReleaseHoursBefore = 0 },
            new() { CompanyId = 7, StartAt = slot.AddDays(-1), EndAt = slot.AddDays(1), UnitsPerSlot = 3, ReleaseHoursBefore = 0 },
        ];

        Assert.Equal(5, AvailabilityCalculator.ReservedUnits(reservations, slot, slot.AddHours(-1), bookingCompanyId: 9));
        Assert.Equal(3, AvailabilityCalculator.ReservedUnits(reservations, slot, slot.AddHours(-1), bookingCompanyId: 9, walkIn: true));
    }
}
