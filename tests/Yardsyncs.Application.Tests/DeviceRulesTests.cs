using Yardsyncs.Application.Gate;
using Yardsyncs.Domain.Gate;

namespace Yardsyncs.Application.Tests;

public class DeviceRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_camera_read_is_normalized_and_misreads_are_flagged()
    {
        var (errors, read) = DeviceRules.Check(LaneDeviceKind.Camera, new DeviceReadInput(
            Plate: "abj-1234", PlateConfidence: 91,
            Containers: [new("cs qu 305438-3", "22g1", 88, "front"), new("CSQU3054384")],
            Seals: [" sl123 ", "SL123"],
            Damage: [new("img/9.jpg", "Dent on door", "REAR")]), Now);

        Assert.Empty(errors);
        Assert.Equal("1234ABJ", read!.Plate);
        Assert.Equal(Now, read.ReadAt);
        Assert.Equal(new ContainerReadView("CSQU3054383", true, "22G1", 88, "FRONT"), read.Containers[0]);
        Assert.False(read.Containers[1].CheckDigitOk);
        Assert.Contains("CSQU3054384", Assert.Single(read.Warnings));
        Assert.Equal(new[] { "SL123" }, read.Seals);
    }

    [Fact]
    public void Each_kind_sends_only_what_it_produces()
    {
        Assert.Contains("does not send tags", Assert.Single(DeviceRules.Check(LaneDeviceKind.Camera, new DeviceReadInput(Tag: "E200"), Now).Errors));
        Assert.Contains("does not send plates", DeviceRules.Check(LaneDeviceKind.RfidReader, new DeviceReadInput(Plate: "1234ABJ"), Now).Errors[0]);
        Assert.Contains("sends no reads", Assert.Single(DeviceRules.Check(LaneDeviceKind.Barrier, new DeviceReadInput(), Now).Errors));
        Assert.Empty(DeviceRules.Check(LaneDeviceKind.Weighbridge, new DeviceReadInput(WeightKg: 31_250m), Now).Errors);
        Assert.Empty(DeviceRules.Check(LaneDeviceKind.Kiosk, new DeviceReadInput(QrToken: "YS1:abc"), Now).Errors);
        Assert.Equal("E200ABC", DeviceRules.Check(LaneDeviceKind.RfidReader, new DeviceReadInput(Tag: " e200abc "), Now).Read!.Tag);
    }

    [Fact]
    public void Bad_values_and_a_wrong_device_clock_are_refused()
    {
        Assert.NotEmpty(DeviceRules.Check(LaneDeviceKind.Camera, new DeviceReadInput(Plate: "1234ABJ", PlateConfidence: 120), Now).Errors);
        Assert.NotEmpty(DeviceRules.Check(LaneDeviceKind.Weighbridge, new DeviceReadInput(WeightKg: -5m), Now).Errors);
        Assert.NotEmpty(DeviceRules.Check(LaneDeviceKind.Camera, new DeviceReadInput(Containers: [new("NOT A CONTAINER NUMBER")]), Now).Errors);
        Assert.Contains("device clock", Assert.Single(DeviceRules.Check(LaneDeviceKind.Camera, new DeviceReadInput(ReadAt: Now.AddHours(1), Plate: "1234ABJ"), Now).Errors));
        Assert.Empty(DeviceRules.Check(LaneDeviceKind.Camera, new DeviceReadInput(ReadAt: Now.AddMinutes(-3), Plate: "1234ABJ"), Now).Errors);
    }

    [Fact]
    public void A_device_is_online_while_it_calls_and_offline_when_silent()
    {
        var device = new GateLaneDevice { KeyHash = "x", LastSeenAt = Now.AddSeconds(-30) };
        Assert.Equal(DeviceConnection.Online, DeviceRules.Connection(device, Now));
        Assert.Equal(DeviceConnection.Fault, DeviceRules.Connection(new GateLaneDevice { KeyHash = "x", LastSeenAt = Now, ReportedStatus = DeviceHealth.Fault }, Now));
        Assert.Equal(DeviceConnection.Offline, DeviceRules.Connection(device, Now.AddMinutes(3)));
        Assert.Equal(DeviceConnection.NotConnected, DeviceRules.Connection(new GateLaneDevice { LastSeenAt = Now }, Now));
    }

    [Fact]
    public void Commands_go_to_the_devices_that_carry_them_out()
    {
        Assert.Equal(new[] { LaneDeviceKind.Barrier }, DeviceRules.TakenBy(LaneCommand.Open));
        Assert.Contains(LaneDeviceKind.Kiosk, DeviceRules.TakenBy(LaneCommand.Show));
        Assert.Equal(new[] { LaneDeviceKind.Intercom }, DeviceRules.TakenBy(LaneCommand.Call));
        Assert.Null(DeviceRules.CommandProblem(LaneCommand.Hangup, null));
        Assert.Equal(TimeSpan.FromSeconds(30), DeviceRules.Lifetime(LaneCommand.Open));
        Assert.Equal(TimeSpan.FromMinutes(2), DeviceRules.Lifetime(LaneCommand.Print));
        Assert.Null(DeviceRules.CommandProblem(LaneCommand.Show, "Go to lane 2"));
        Assert.NotNull(DeviceRules.CommandProblem(LaneCommand.Show, " "));
        Assert.NotNull(DeviceRules.CommandProblem(LaneCommand.Open, "now"));
    }

    [Fact]
    public void Command_progress()
    {
        var c = new LaneDeviceCommand { ExpiresAt = Now.AddSeconds(30) };
        Assert.Equal(CommandProgress.Waiting, DeviceRules.Progress(c, Now));
        c.DeliveredAt = Now;
        Assert.Equal(CommandProgress.Delivered, DeviceRules.Progress(c, Now));
        Assert.Equal(CommandProgress.Expired, DeviceRules.Progress(c, Now.AddMinutes(1)));
        c.Result = CommandResult.Done;
        Assert.Equal(CommandProgress.Done, DeviceRules.Progress(c, Now.AddMinutes(1)));
    }

    [Fact]
    public void Device_keys_are_random_and_only_their_fingerprint_is_kept()
    {
        var a = DeviceKeys.New();
        var b = DeviceKeys.New();
        Assert.NotEqual(a, b);
        Assert.StartsWith("ysd_", a);
        Assert.True(DeviceKeys.LooksLikeKey(a));
        Assert.False(DeviceKeys.LooksLikeKey("Bearer abc"));
        Assert.Equal(64, DeviceKeys.Hash(a).Length);
        Assert.Equal(DeviceKeys.Hash(a), DeviceKeys.Hash($" {a} "));
    }
}
