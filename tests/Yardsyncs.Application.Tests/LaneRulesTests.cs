using Yardsyncs.Application.Gate;
using Yardsyncs.Domain;
using Yardsyncs.Domain.Gate;

namespace Yardsyncs.Application.Tests;

public class LaneRulesTests
{
    private static readonly TerminalSettings Defaults = new(30, 30, 60, 6);

    private static GateLaneDevice Device(LaneDeviceKind kind, string? reads = null, bool active = true) =>
        new() { DeviceCode = $"{kind}", Kind = kind, Reads = reads, IsActive = active };

    [Fact]
    public void Reads_are_stored_as_upper_snake_words_and_read_back()
    {
        var stored = LaneRules.FormatReads([LaneRead.IsoCode, LaneRead.Plate, LaneRead.Plate]);

        Assert.Equal("PLATE,ISO_CODE", stored);
        Assert.Equal(new[] { LaneRead.Plate, LaneRead.IsoCode }, LaneRules.ParseReads(stored));
        Assert.Equal(new[] { LaneRead.Container }, LaneRules.ParseReads(" container , WHATEVER "));
        Assert.Null(LaneRules.FormatReads([]));
        Assert.Empty(LaneRules.ParseReads(null));
    }

    [Fact]
    public void Reads_must_fit_the_kind_of_device()
    {
        Assert.Empty(LaneRules.Validate("CAM-1", LaneDeviceKind.Camera, [LaneRead.Plate, LaneRead.Damage]));
        Assert.Empty(LaneRules.Validate("RFID-1", LaneDeviceKind.RfidReader, [LaneRead.Tag]));
        Assert.Contains("RFID reader cannot read plates", Assert.Single(LaneRules.Validate("RFID-1", LaneDeviceKind.RfidReader, [LaneRead.Plate])));
        Assert.Contains("barrier reads nothing", Assert.Single(LaneRules.Validate("BAR-1", LaneDeviceKind.Barrier, [LaneRead.Tag])));
        Assert.Contains("code is required", Assert.Single(LaneRules.Validate("  ", LaneDeviceKind.Kiosk, null)));
        Assert.Single(LaneRules.Validate(new string('X', 31), LaneDeviceKind.Kiosk, null));
    }

    [Fact]
    public void A_manual_lane_identifies_by_number_and_gate_pass_even_without_devices()
    {
        Assert.Equal(new[] { LaneIdentification.AppointmentNo, LaneIdentification.GatePass }, LaneRules.Identifies(LaneMode.Manual, [], Defaults));
        Assert.Equal(new[] { LaneIdentification.GatePass },
            LaneRules.Identifies(LaneMode.Manual, [], Defaults with { GateByNumber = false }));
    }

    [Fact]
    public void Cameras_add_plate_and_container_as_the_terminal_allows()
    {
        GateLaneDevice[] devices = [Device(LaneDeviceKind.Camera, "PLATE"), Device(LaneDeviceKind.Ocr, "CONTAINER,ISO_CODE"), Device(LaneDeviceKind.RfidReader, "TAG")];

        Assert.Equal(new[] { LaneIdentification.Plate, LaneIdentification.Container, LaneIdentification.RfidTag },
            LaneRules.Identifies(LaneMode.Automatic, devices, Defaults));
        Assert.Equal(new[] { LaneIdentification.Container, LaneIdentification.RfidTag },
            LaneRules.Identifies(LaneMode.Automatic, devices, Defaults with { GateByPlate = false }));
        Assert.Empty(LaneRules.Identifies(LaneMode.Automatic, [Device(LaneDeviceKind.Camera, "PLATE", active: false)], Defaults));
    }

    [Fact]
    public void A_kiosk_lets_an_automatic_lane_take_numbers_and_gate_passes()
    {
        Assert.Equal(new[] { LaneIdentification.AppointmentNo, LaneIdentification.GatePass },
            LaneRules.Identifies(LaneMode.Automatic, [Device(LaneDeviceKind.Kiosk)], Defaults));
    }

    [Fact]
    public void An_automatic_lane_needs_a_barrier_and_a_way_to_identify_trucks()
    {
        Assert.Equal(new[] { LaneProblem.NoBarrier, LaneProblem.NoIdentification }, LaneRules.AutomaticProblems([], Defaults));
        Assert.Empty(LaneRules.AutomaticProblems([Device(LaneDeviceKind.Barrier), Device(LaneDeviceKind.Kiosk)], Defaults));
        Assert.Empty(LaneRules.AutomaticProblems([Device(LaneDeviceKind.Barrier), Device(LaneDeviceKind.Camera, "PLATE")], Defaults));

        // Plate reads don't help a terminal that doesn't match by plate; RFID tags aren't matched yet; inactive devices don't count.
        Assert.Equal(new[] { LaneProblem.NoIdentification }, LaneRules.AutomaticProblems(
            [Device(LaneDeviceKind.Barrier), Device(LaneDeviceKind.Camera, "PLATE")], Defaults with { GateByPlate = false }));
        Assert.Equal(new[] { LaneProblem.NoIdentification },
            LaneRules.AutomaticProblems([Device(LaneDeviceKind.Barrier), Device(LaneDeviceKind.RfidReader, "TAG")], Defaults));
        Assert.Equal(new[] { LaneProblem.NoBarrier }, LaneRules.AutomaticProblems(
            [Device(LaneDeviceKind.Barrier, active: false), Device(LaneDeviceKind.Kiosk)], Defaults));

        Assert.StartsWith("a barrier and a way to identify trucks", LaneRules.Needs([LaneProblem.NoBarrier, LaneProblem.NoIdentification]));
    }

    [Fact]
    public void A_closed_lane_says_why()
    {
        Assert.Null(LaneRules.NotOpen(new GateLane { LaneCode = "IN-1" }));
        Assert.Equal("Lane IN-2 is closed. Night shift.",
            LaneRules.NotOpen(new GateLane { LaneCode = "IN-2", State = LaneState.Closed, StateNote = "Night shift." }));
        Assert.Equal("Lane IN-3 is out of order.", LaneRules.NotOpen(new GateLane { LaneCode = "IN-3", State = LaneState.OutOfOrder }));
    }
}
