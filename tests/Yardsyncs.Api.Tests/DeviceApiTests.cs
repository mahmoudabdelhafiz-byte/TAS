using System.Net;
using System.Net.Http.Json;
using Yardsyncs.Api;
using Yardsyncs.Application.Gate;
using Yardsyncs.Domain.Gate;
using Yardsyncs.Infrastructure;

namespace Yardsyncs.Api.Tests;

/// <summary>The device API: keys, reads, status, commands out and confirmed.</summary>
public sealed class DeviceApiTests(YardsyncsApiFactory factory) : ApiTestBase(factory)
{
    private string Lanes => $"{Terminal}/gate/lanes";

    private async Task<(LaneView Lane, HttpClient Device)> DeviceClient(HttpClient admin, string laneCode, string deviceCode)
    {
        var lane = (await Read<List<LaneView>>(await admin.GetAsync(Lanes))).Single(l => l.LaneCode == laneCode);
        var device = lane.Devices.Single(d => d.DeviceCode == deviceCode);
        var issued = await admin.PostAsync($"{Lanes}/{lane.LaneId}/devices/{device.DeviceId}/key", null);
        Assert.Equal(HttpStatusCode.Created, issued.StatusCode);
        var key = (await Read<AdminResponse<DeviceKeyView>>(issued)).Data.Key;
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Add(DeviceEndpoints.KeyHeader, key);
        return (lane, client);
    }

    [Fact]
    public async Task A_camera_signs_in_with_its_key_and_sends_reads()
    {
        if (!Enabled) return;
        var admin = await As(DemoDataSeeder.TerminalAdminEmail);
        var planner = await As(DemoDataSeeder.PlannerEmail);

        // No key, a made-up key: refused.
        Assert.Equal(HttpStatusCode.Unauthorized, (await Factory.CreateClient().GetAsync("/api/devices/me")).StatusCode);
        var stranger = Factory.CreateClient();
        stranger.DefaultRequestHeaders.Add(DeviceEndpoints.KeyHeader, DeviceKeys.New());
        Assert.Equal(HttpStatusCode.Unauthorized, (await stranger.GetAsync("/api/devices/me")).StatusCode);

        // Only admins issue keys.
        var in1 = (await Read<List<LaneView>>(await admin.GetAsync(Lanes))).Single(l => l.LaneCode == "IN-1");
        var cameraId = in1.Devices.Single(d => d.DeviceCode == "IN1-CAM-FRONT").DeviceId;
        Assert.Equal(HttpStatusCode.Forbidden, (await planner.PostAsync($"{Lanes}/{in1.LaneId}/devices/{cameraId}/key", null)).StatusCode);

        var (lane, camera) = await DeviceClient(admin, "IN-1", "IN1-CAM-FRONT");
        var me = await Read<DeviceIdentity>(await camera.GetAsync("/api/devices/me"));
        Assert.Equal(("IN-1", "IN1-CAM-FRONT", LaneDeviceKind.Camera), (me.LaneCode, me.DeviceCode, me.Kind));

        var read = await camera.PostAsJsonAsync("/api/devices/reads", new
        {
            plate = "1234 abj", plateConfidence = 93,
            containers = new[] { new { number = "MSCU 123456-6", isoCode = "22g1" }, new { number = "MSCU1234567", isoCode = (string?)null } },
            photos = new[] { "https://ocr.local/img/1.jpg" },
        });
        Assert.Equal(HttpStatusCode.Created, read.StatusCode);
        var view = (await Read<AdminResponse<DeviceReadView>>(read)).Data;
        Assert.Equal("1234ABJ", view.Plate);
        Assert.Equal("22G1", view.Containers[0].IsoCode);
        Assert.False(view.Containers[1].CheckDigitOk);
        Assert.Single(view.Warnings);

        // A camera doesn't weigh trucks.
        Assert.Equal(HttpStatusCode.BadRequest, (await camera.PostAsJsonAsync("/api/devices/reads", new { weightKg = 30000 })).StatusCode);

        // The gate sees the lane's latest read and the camera online; a fault shows too.
        var seen = (await Read<List<LaneView>>(await planner.GetAsync(Lanes))).Single(l => l.LaneCode == "IN-1");
        Assert.Equal("1234ABJ", seen.LastRead!.Plate);
        Assert.Equal(DeviceConnection.Online, seen.Devices.Single(d => d.DeviceCode == "IN1-CAM-FRONT").Connection);
        Assert.Equal(DeviceConnection.NotConnected, seen.Devices.Single(d => d.DeviceCode == "IN1-BARRIER").Connection);
        Assert.Equal(HttpStatusCode.OK, (await camera.PostAsJsonAsync("/api/devices/status", new { status = "Fault", detail = "Lens dirty" })).StatusCode);
        var faulty = (await Read<List<LaneView>>(await planner.GetAsync(Lanes))).Single(l => l.LaneCode == "IN-1").Devices.Single(d => d.DeviceCode == "IN1-CAM-FRONT");
        Assert.Equal((DeviceConnection.Fault, "Lens dirty"), (faulty.Connection, faulty.StatusDetail));

        // A new key replaces the old one.
        var (_, renewed) = await DeviceClient(admin, "IN-1", "IN1-CAM-FRONT");
        Assert.Equal(HttpStatusCode.Unauthorized, (await camera.GetAsync("/api/devices/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await renewed.GetAsync("/api/devices/me")).StatusCode);

        var activity = await Read<LaneActivity>(await planner.GetAsync($"{Lanes}/{lane.LaneId}/activity"));
        Assert.Equal("1234ABJ", Assert.Single(activity.Reads).Plate);
    }

    [Fact]
    public async Task Gate_staff_open_the_barrier_and_the_barrier_confirms()
    {
        if (!Enabled) return;
        var admin = await As(DemoDataSeeder.TerminalAdminEmail);
        var planner = await As(DemoDataSeeder.PlannerEmail);
        var (lane, barrier) = await DeviceClient(admin, "IN-1", "IN1-BARRIER");

        Assert.Empty(await Read<List<DeviceCommandView>>(await barrier.GetAsync("/api/devices/commands")));

        // Open goes to the lane's barrier; Show needs a text; IN-2 has no barrier.
        var sent = await planner.PostAsJsonAsync($"{Lanes}/{lane.LaneId}/commands", new { command = "Open" });
        Assert.Equal(HttpStatusCode.Created, sent.StatusCode);
        Assert.Equal("IN1-BARRIER", Assert.Single((await Read<AdminResponse<List<LaneCommandView>>>(sent)).Data).DeviceCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await planner.PostAsJsonAsync($"{Lanes}/{lane.LaneId}/commands", new { command = "Show" })).StatusCode);
        var in2 = (await Read<List<LaneView>>(await planner.GetAsync(Lanes))).Single(l => l.LaneCode == "IN-2");
        Assert.Equal(HttpStatusCode.Conflict, (await planner.PostAsJsonAsync($"{Lanes}/{in2.LaneId}/commands", new { command = "Open" })).StatusCode);

        // Company users don't open barriers.
        var dispatcher = await As(DemoDataSeeder.DispatcherEmail);
        Assert.Equal(HttpStatusCode.Forbidden, (await dispatcher.PostAsJsonAsync($"{Lanes}/{lane.LaneId}/commands", new { command = "Open" })).StatusCode);

        var pending = Assert.Single(await Read<List<DeviceCommandView>>(await barrier.GetAsync("/api/devices/commands")));
        Assert.Equal(LaneCommand.Open, pending.Command);
        var delivered = Assert.Single((await Read<LaneActivity>(await planner.GetAsync($"{Lanes}/{lane.LaneId}/activity"))).Commands);
        Assert.Equal(CommandProgress.Delivered, delivered.Progress);

        Assert.Equal(HttpStatusCode.OK, (await barrier.PostAsJsonAsync($"/api/devices/commands/{pending.CommandId}/ack", new { done = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await barrier.PostAsJsonAsync($"/api/devices/commands/{pending.CommandId}/ack", new { done = true })).StatusCode);
        Assert.Empty(await Read<List<DeviceCommandView>>(await barrier.GetAsync("/api/devices/commands")));
        Assert.Equal(CommandProgress.Done, Assert.Single((await Read<LaneActivity>(await planner.GetAsync($"{Lanes}/{lane.LaneId}/activity"))).Commands).Progress);

        // Another device can't confirm the barrier's commands.
        var (_, light) = await DeviceClient(admin, "IN-1", "IN1-LIGHT");
        Assert.Equal(HttpStatusCode.NotFound, (await light.PostAsJsonAsync($"/api/devices/commands/{pending.CommandId}/ack", new { done = true })).StatusCode);
    }
}
