using Yardsyncs.Application.Gate;
using Yardsyncs.Domain.Gate;

namespace Yardsyncs.Application.Tests;

public class LaneControllerRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 10, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(30);

    private static NormalizedRead Read(string? plate = null, int? confidence = null, string[]? containers = null, string? appointmentNo = null, string? qr = null) =>
        new(Now, plate, confidence, (containers ?? []).Select(c => new ContainerReadView(c, true, null, null, null)).ToList(),
            [], [], [], null, appointmentNo, qr, null, []);

    [Fact]
    public void Reads_add_up_into_one_truck()
    {
        var t = new LaneTransaction { StartedAt = Now, LastReadAt = Now };
        LaneControllerRules.Merge(t, Read("1234ABJ", 60), Now);
        LaneControllerRules.Merge(t, Read("1234ABJ", 95, ["MSCU1234566"]), Now.AddSeconds(2));
        LaneControllerRules.Merge(t, Read(containers: ["MSCU1234566", "CSQU3054383"]), Now.AddSeconds(3));

        Assert.Equal(("1234ABJ", (short?)95, 3), (t.Plate, t.PlateConfidence, t.ReadCount));
        Assert.Equal(new[] { "MSCU1234566", "CSQU3054383" }, LaneControllerRules.ContainersOf(t));
        Assert.Equal(Now.AddSeconds(3), t.LastReadAt);
    }

    [Fact]
    public void A_read_belongs_to_the_lane_s_current_truck_unless_it_is_late_or_another_plate()
    {
        var t = new LaneTransaction { Plate = "1234ABJ", LastReadAt = Now, Status = LaneTransactionStatus.Referred };
        Assert.True(LaneControllerRules.Belongs(t, Read(containers: ["MSCU1234566"]), Now.AddSeconds(10), Window));
        Assert.False(LaneControllerRules.Belongs(t, Read("5678HDR"), Now.AddSeconds(10), Window));
        Assert.False(LaneControllerRules.Belongs(t, Read("1234ABJ"), Now.AddMinutes(1), Window));
        Assert.False(LaneControllerRules.Belongs(new LaneTransaction { Plate = "1234ABJ", LastReadAt = Now, Status = LaneTransactionStatus.Passed }, Read("1234ABJ"), Now, Window));
    }

    [Fact]
    public void The_lane_decides_when_quiet_or_at_once_for_a_kiosk_entry()
    {
        var settle = TimeSpan.FromSeconds(3);
        var plate = new LaneTransaction { Plate = "1234ABJ", LastReadAt = Now };
        Assert.False(LaneControllerRules.Ready(plate, Now.AddSeconds(1), settle));
        Assert.True(LaneControllerRules.Ready(plate, Now.AddSeconds(3), settle));
        Assert.True(LaneControllerRules.Ready(new LaneTransaction { AppointmentRead = "DEMO-1", LastReadAt = Now }, Now, settle));
        Assert.False(LaneControllerRules.Ready(new LaneTransaction { WeightKg = 30000, LastReadAt = Now }, Now.AddMinutes(1), settle));

        var nothing = new LaneTransaction { WeightKg = 30000, StartedAt = Now, LastReadAt = Now };
        Assert.False(LaneControllerRules.Abandon(nothing, Now.AddSeconds(60), TimeSpan.FromSeconds(120)));
        Assert.True(LaneControllerRules.Abandon(nothing, Now.AddSeconds(120), TimeSpan.FromSeconds(120)));
    }

    [Fact]
    public void Admitted_opens_the_barrier_on_green_and_prints_the_ticket_at_the_entry()
    {
        var entry = LaneControllerRules.Plan(LaneDirection.In, true, "DEMO-261020-00001", null, "ticket");
        Assert.Equal(new[] { LaneCommand.Green, LaneCommand.Open, LaneCommand.Show, LaneCommand.Print }, entry.Select(c => c.Command));
        Assert.Equal("DEMO-261020-00001: go ahead.", entry[2].Text);

        var exit = LaneControllerRules.Plan(LaneDirection.Out, true, "DEMO-261020-00001", null, "ticket");
        Assert.Equal(new[] { LaneCommand.Green, LaneCommand.Open, LaneCommand.Show }, exit.Select(c => c.Command));

        var referred = LaneControllerRules.Plan(LaneDirection.In, false, null, "Early: the arrival window opens at 09:30.", null);
        Assert.Equal(new[] { LaneCommand.Red, LaneCommand.Show }, referred.Select(c => c.Command));
        Assert.Equal("Please wait: a clerk will help you. Early: the arrival window opens at 09:30.", referred[1].Text);
        Assert.Equal(400, LaneControllerRules.Display.Refer(new string('x', 600)).Length);
    }

    [Fact]
    public void The_gate_ticket_lists_what_is_known()
    {
        var ticket = LaneControllerRules.Display.Ticket("Demo Terminal", "IN-1", "DEMO-1", "1234ABJ", ["MSCU1234566"], "2026-10-20 13:00");
        Assert.Equal("Demo Terminal\nGate ticket · lane IN-1 · 2026-10-20 13:00\nAppointment: DEMO-1\nTruck: 1234ABJ\nContainers: MSCU1234566", ticket);
    }
}
