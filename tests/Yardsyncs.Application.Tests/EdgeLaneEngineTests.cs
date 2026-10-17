using Yardsyncs.Application.Edge;
using Yardsyncs.Application.Gate;
using Yardsyncs.Domain;
using Yardsyncs.Domain.Booking;
using Yardsyncs.Domain.Gate;

namespace Yardsyncs.Application.Tests;

public class EdgeLaneEngineTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 10, 0, 0, TimeSpan.FromHours(3));
    private static readonly byte[] PassSecret = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();

    private const string CameraKey = "ysd_camera0000000000000000000000000000000000";
    private const string KioskKey = "ysd_kiosk00000000000000000000000000000000000";
    private const string BarrierKey = "ysd_barrier000000000000000000000000000000000";
    private const string ExitCameraKey = "ysd_exitcam000000000000000000000000000000000";
    private const string ExitBarrierKey = "ysd_exitbar000000000000000000000000000000000";

    private static long _ids;

    private static EdgeAppointment Appointment(string no, string plate, AppointmentStatus status = AppointmentStatus.Confirmed, int slotHour = 10,
        TrafficLight light = TrafficLight.Green, string? mobile = "+966500000001") =>
        new(++_ids, no, $"QR-{no}", status, new DateTimeOffset(2026, 10, 20, slotHour, 0, 0, TimeSpan.FromHours(3)),
            new DateTimeOffset(2026, 10, 20, slotHour + 1, 0, 0, TimeSpan.FromHours(3)),
            plate, ["MSCU1234566"], true, mobile, false, false, light, light == TrafficLight.Red ? "Customs hold" : null);

    private static EdgeSnapshot Snapshot(params EdgeAppointment[] appointments) => new(
        1, "DEMO", "Demo Terminal", "Asia/Riyadh", TerminalSettings.Defaults, Now.AddMinutes(-5), Now.AddHours(36),
        [
            new EdgeLane("IN-1", LaneDirection.In, LaneMode.Automatic, LaneState.Open, true),
            new EdgeLane("OUT-1", LaneDirection.Out, LaneMode.Automatic, LaneState.Open, true),
            new EdgeLane("IN-2", LaneDirection.In, LaneMode.Manual, LaneState.Open, true),
        ],
        [
            new EdgeDevice("IN1-CAM", "IN-1", LaneDeviceKind.Camera, DeviceKeys.Hash(CameraKey)),
            new EdgeDevice("IN1-KIOSK", "IN-1", LaneDeviceKind.Kiosk, DeviceKeys.Hash(KioskKey)),
            new EdgeDevice("IN1-BARRIER", "IN-1", LaneDeviceKind.Barrier, DeviceKeys.Hash(BarrierKey)),
            new EdgeDevice("IN1-PRINTER", "IN-1", LaneDeviceKind.Printer, DeviceKeys.Hash("ysd_printer000000000000000000000000000000000")),
            new EdgeDevice("OUT1-CAM", "OUT-1", LaneDeviceKind.Camera, DeviceKeys.Hash(ExitCameraKey)),
            new EdgeDevice("OUT1-BARRIER", "OUT-1", LaneDeviceKind.Barrier, DeviceKeys.Hash(ExitBarrierKey)),
        ],
        appointments,
        [new EdgePass(77, Convert.ToBase64String(PassSecret), "+966500000001")]);

    private static EdgeLaneEngine Engine(params EdgeAppointment[] appointments) => new(Snapshot(appointments), settleSeconds: 0);

    [Fact]
    public void Only_known_device_keys_get_in()
    {
        var engine = Engine();
        Assert.Equal("IN1-CAM", engine.Authenticate(CameraKey)!.DeviceCode);
        Assert.Null(engine.Authenticate("ysd_unknown00000000000000000000000000000000"));
        Assert.Null(engine.Authenticate("not a key"));
    }

    [Fact]
    public void A_truck_on_time_is_let_in_its_barrier_sends_it_through_and_the_ticket_follows()
    {
        var engine = Engine(Appointment("DEMO-1", "1234ABJ"));
        var camera = engine.Authenticate(CameraKey)!;
        var barrier = engine.Authenticate(BarrierKey)!;

        var (errors, read) = engine.OnRead(camera, new DeviceReadInput(Plate: "1234 ABJ", PlateConfidence: 95), Now);
        Assert.Empty(errors);
        Assert.True(read!.ReadId < 0);

        var passage = Assert.Single(engine.Passages);
        Assert.Equal((true, "DEMO-1", LaneDirection.In, (DateTimeOffset?)null), (passage.Admitted, passage.AppointmentNo, passage.Direction, passage.PassedAt));
        Assert.Contains("without the cloud", passage.Message);
        var open = Assert.Single(engine.Pending(barrier, Now));
        Assert.Equal(LaneCommand.Open, open.Command);
        Assert.True(open.CommandId < 0);
        Assert.Equal("DEMO-1: go ahead.", Assert.Single(engine.Pending(engine.Authenticate(KioskKey)!, Now)).Text);

        Assert.True(engine.Ack(barrier, open.CommandId, true, Now.AddSeconds(4)));
        Assert.Equal(Now.AddSeconds(4), engine.Passages.Single().PassedAt);
        Assert.Empty(engine.Pending(barrier, Now.AddSeconds(5)));
        var printer = engine.Authenticate("ysd_printer000000000000000000000000000000000")!;
        Assert.Contains("Appointment: DEMO-1", Assert.Single(engine.Pending(printer, Now.AddSeconds(5))).Text);

        // The same appointment can't be used twice while offline.
        engine.OnRead(camera, new DeviceReadInput(Plate: "1234 ABJ"), Now.AddMinutes(2));
        var again = engine.Passages.Last();
        Assert.Equal((false, "ALREADY_IN"), (again.Admitted, again.Reason));
    }

    [Fact]
    public void The_rules_are_the_clouds()
    {
        var engine = Engine(Appointment("EARLY-1", "1111AAA", slotHour: 16), Appointment("HOLD-1", "2222BBB", light: TrafficLight.Red));
        var camera = engine.Authenticate(CameraKey)!;

        engine.OnRead(camera, new DeviceReadInput(Plate: "1111 AAA"), Now);
        Assert.Equal((false, "EARLY"), (engine.Passages[0].Admitted, engine.Passages[0].Reason));
        engine.OnRead(camera, new DeviceReadInput(Plate: "2222 BBB"), Now.AddMinutes(1));
        Assert.Equal((false, "NOT_CLEARED"), (engine.Passages[1].Admitted, engine.Passages[1].Reason));
        Assert.Contains("Customs hold", engine.Passages[1].Message);
        engine.OnRead(camera, new DeviceReadInput(Plate: "9999 ZZZ"), Now.AddMinutes(2));
        Assert.Equal((false, "NO_APPOINTMENT"), (engine.Passages[2].Admitted, engine.Passages[2].Reason));
        Assert.Empty(engine.Pending(engine.Authenticate(BarrierKey)!, Now)); // the barrier stays shut when referred
    }

    [Fact]
    public void The_drivers_gate_pass_is_checked_at_the_kiosk()
    {
        var engine = Engine(Appointment("DEMO-2", "3333CCC"), Appointment("OTHER-1", "4444DDD", mobile: "+966500000009"));
        var kiosk = engine.Authenticate(KioskKey)!;

        var live = GatePassCode.Payload(77, "DEMO-2", GatePassCode.Compute(PassSecret, GatePassCode.Step(Now)));
        engine.OnRead(kiosk, new DeviceReadInput(QrToken: live), Now);
        Assert.Equal((true, "DEMO-2"), (engine.Passages[0].Admitted, engine.Passages[0].AppointmentNo));

        var stale = GatePassCode.Payload(77, "OTHER-1", GatePassCode.Compute(PassSecret, GatePassCode.Step(Now) - 10));
        engine.OnRead(kiosk, new DeviceReadInput(QrToken: stale), Now.AddMinutes(1));
        Assert.False(engine.Passages[1].Admitted);
        Assert.Contains("could not be checked", engine.Passages[1].Message);

        var notHis = GatePassCode.Payload(77, "OTHER-1", GatePassCode.Compute(PassSecret, GatePassCode.Step(Now.AddMinutes(2))));
        engine.OnRead(kiosk, new DeviceReadInput(QrToken: notHis), Now.AddMinutes(2));
        Assert.Contains("not this driver's", engine.Passages[2].Message);
    }

    [Fact]
    public void Exits_find_the_trucks_inside_including_those_let_in_offline()
    {
        var engine = Engine(Appointment("IN-ALREADY", "5555EEE", AppointmentStatus.GateIn), Appointment("DEMO-3", "6666FFF"));
        var exit = engine.Authenticate(ExitCameraKey)!;

        engine.OnRead(exit, new DeviceReadInput(Plate: "5555 EEE"), Now);
        Assert.Equal((true, "IN-ALREADY", LaneDirection.Out), (engine.Passages[0].Admitted, engine.Passages[0].AppointmentNo, engine.Passages[0].Direction));
        Assert.Equal(LaneCommand.Open, Assert.Single(engine.Pending(engine.Authenticate(ExitBarrierKey)!, Now)).Command);

        engine.OnRead(exit, new DeviceReadInput(Plate: "6666 FFF"), Now.AddMinutes(1));
        Assert.Equal((false, "EXIT"), (engine.Passages[1].Admitted, engine.Passages[1].Reason)); // not inside

        engine.OnRead(engine.Authenticate(CameraKey)!, new DeviceReadInput(Plate: "6666 FFF"), Now.AddMinutes(2));
        engine.OnRead(exit, new DeviceReadInput(Plate: "6666 FFF"), Now.AddMinutes(40));
        Assert.True(engine.Passages[3].Admitted);
    }

    [Fact]
    public void Reads_at_a_clerks_lane_are_only_stored_and_a_restart_keeps_who_is_in()
    {
        var snapshot = Snapshot(Appointment("DEMO-4", "7777GGG"));
        var engine = new EdgeLaneEngine(snapshot, settleSeconds: 0);
        var manual = new EdgeLaneEngine(snapshot with { Devices = [new EdgeDevice("IN2-CAM", "IN-2", LaneDeviceKind.Camera, DeviceKeys.Hash(CameraKey))] }, settleSeconds: 0);
        manual.OnRead(manual.Authenticate(CameraKey)!, new DeviceReadInput(Plate: "7777 GGG"), Now);
        Assert.Empty(manual.Passages);

        engine.OnRead(engine.Authenticate(CameraKey)!, new DeviceReadInput(Plate: "7777 GGG"), Now);
        var restarted = new EdgeLaneEngine(snapshot, engine.Passages, settleSeconds: 0);
        restarted.OnRead(restarted.Authenticate(CameraKey)!, new DeviceReadInput(Plate: "7777 GGG"), Now.AddMinutes(3));
        Assert.Equal("ALREADY_IN", restarted.Passages.Last().Reason);

        restarted.Forget([restarted.Passages[0].PassageId]);
        Assert.Single(restarted.Passages);
    }

    [Fact]
    public void A_settle_time_waits_for_the_rear_camera()
    {
        var engine = new EdgeLaneEngine(Snapshot(Appointment("DEMO-5", "8888HHH")), settleSeconds: 3);
        var camera = engine.Authenticate(CameraKey)!;
        engine.OnRead(camera, new DeviceReadInput(Plate: "8888 HHH"), Now);
        Assert.Empty(engine.Passages);
        engine.OnRead(camera, new DeviceReadInput(Containers: [new ContainerReadInput("MSCU1234566")]), Now.AddSeconds(1));
        engine.Tick(Now.AddSeconds(2));
        Assert.Empty(engine.Passages);
        engine.Tick(Now.AddSeconds(5));
        Assert.Equal(new[] { "MSCU1234566" }, Assert.Single(engine.Passages).Containers);
    }
}
