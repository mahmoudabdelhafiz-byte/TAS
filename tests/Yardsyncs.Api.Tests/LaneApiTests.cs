using System.Net;
using System.Net.Http.Json;
using Yardsyncs.Api;
using Yardsyncs.Application.Gate;
using Yardsyncs.Domain.Gate;
using Yardsyncs.Infrastructure;

namespace Yardsyncs.Api.Tests;

/// <summary>The Autogate lane model: lane states, devices per lane, and switching a lane to automatic.</summary>
public sealed class LaneApiTests(YardsyncsApiFactory factory) : ApiTestBase(factory)
{
    private string Lanes => $"{Terminal}/gate/lanes";

    private async Task<LaneView> Lane(HttpClient client, string code) =>
        (await Read<List<LaneView>>(await client.GetAsync(Lanes))).Single(l => l.LaneCode == code);

    [Fact]
    public async Task Gate_staff_close_a_lane_and_trucks_are_sent_elsewhere()
    {
        if (!Enabled) return;
        var planner = await As(DemoDataSeeder.PlannerEmail);
        var in2 = await Lane(planner, "IN-2");
        Assert.Equal(LaneState.Open, in2.State);

        // Closing needs a reason.
        Assert.Equal(HttpStatusCode.BadRequest, (await planner.PostAsJsonAsync($"{Lanes}/{in2.LaneId}/state", new { state = "Closed" })).StatusCode);
        var closed = await planner.PostAsJsonAsync($"{Lanes}/{in2.LaneId}/state", new { state = "OutOfOrder", note = "Barrier arm broken." });
        Assert.Equal(HttpStatusCode.OK, closed.StatusCode);
        Assert.Equal(LaneState.OutOfOrder, (await Read<AdminResponse<LaneView>>(closed)).Data.State);

        var arrival = await planner.PostAsJsonAsync($"{Terminal}/gate/arrivals", new { laneCode = "IN-2", plateRead = "1234 ABJ" });
        Assert.Equal(HttpStatusCode.Conflict, arrival.StatusCode);
        Assert.Contains("Barrier arm broken.", await arrival.Content.ReadAsStringAsync());

        var reopened = await planner.PostAsJsonAsync($"{Lanes}/{in2.LaneId}/state", new { state = "Open" });
        var view = (await Read<AdminResponse<LaneView>>(reopened)).Data;
        Assert.Equal(LaneState.Open, view.State);
        Assert.Null(view.StateNote);
        Assert.Equal(HttpStatusCode.OK, (await planner.PostAsJsonAsync($"{Terminal}/gate/arrivals", new { laneCode = "IN-2", plateRead = "9999 ZZZ" })).StatusCode);

        // Company users can't touch the gate.
        var dispatcher = await As(DemoDataSeeder.DispatcherEmail);
        Assert.Equal(HttpStatusCode.Forbidden, (await dispatcher.PostAsJsonAsync($"{Lanes}/{in2.LaneId}/state", new { state = "Open" })).StatusCode);
    }

    [Fact]
    public async Task Admins_equip_a_lane_and_switch_it_to_automatic()
    {
        if (!Enabled) return;
        var admin = await As(DemoDataSeeder.TerminalAdminEmail);
        var planner = await As(DemoDataSeeder.PlannerEmail);

        // The demo's IN-1 is equipped; IN-2 is a plain booth lane.
        var in1 = await Lane(admin, "IN-1");
        Assert.Equal(5, in1.Devices.Count);
        Assert.Contains(LaneIdentification.Plate, in1.Identifies);
        Assert.Empty(in1.AutomaticProblems);
        var in2 = await Lane(admin, "IN-2");
        Assert.Equal(2, in2.AutomaticProblems.Count);

        // Only admins set lanes up.
        Assert.Equal(HttpStatusCode.Forbidden, (await planner.PutAsJsonAsync($"{Lanes}/{in2.LaneId}", new { mode = "Automatic" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await planner.PostAsJsonAsync($"{Lanes}/{in2.LaneId}/devices", new { deviceCode = "X", kind = "Kiosk" })).StatusCode);

        // Not ready yet.
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"{Lanes}/{in2.LaneId}", new { mode = "Automatic" })).StatusCode);

        // Devices: reads must fit, codes are unique at the terminal.
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"{Lanes}/{in2.LaneId}/devices", new { deviceCode = "IN2-BAR", kind = "Barrier", reads = new[] { "Plate" } })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"{Lanes}/{in2.LaneId}/devices", new { deviceCode = "in1-kiosk", kind = "Kiosk" })).StatusCode);
        var barrier = await admin.PostAsJsonAsync($"{Lanes}/{in2.LaneId}/devices", new { deviceCode = "IN2-BAR", kind = "Barrier", vendor = "Gatemaker", address = "10.0.4.21" });
        Assert.Equal(HttpStatusCode.Created, barrier.StatusCode);
        var camera = await admin.PostAsJsonAsync($"{Lanes}/{in2.LaneId}/devices", new { deviceCode = "IN2-CAM", kind = "Camera", reads = new[] { "Plate", "Damage" } });
        var equipped = (await Read<AdminResponse<LaneView>>(camera)).Data;
        Assert.Empty(equipped.AutomaticProblems);
        Assert.Equal(new[] { LaneRead.Plate, LaneRead.Damage }, equipped.Devices.Single(d => d.DeviceCode == "IN2-CAM").Reads);

        var automatic = await admin.PutAsJsonAsync($"{Lanes}/{in2.LaneId}", new { mode = "Automatic" });
        Assert.Equal(HttpStatusCode.OK, automatic.StatusCode);
        Assert.Equal(LaneMode.Automatic, (await Read<AdminResponse<LaneView>>(automatic)).Data.Mode);

        // An automatic lane keeps what it needs: the barrier can't go, nor can the camera be switched off.
        var barrierId = equipped.Devices.Single(d => d.DeviceCode == "IN2-BAR").DeviceId;
        var cameraId = equipped.Devices.Single(d => d.DeviceCode == "IN2-CAM").DeviceId;
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"{Lanes}/{in2.LaneId}/devices/{barrierId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync($"{Lanes}/{in2.LaneId}/devices/{cameraId}",
            new { deviceCode = "IN2-CAM", kind = "Camera", reads = new[] { "Plate" }, isActive = false })).StatusCode);

        // Back to manual, then the barrier can go.
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"{Lanes}/{in2.LaneId}", new { mode = "Manual" })).StatusCode);
        var removed = await admin.DeleteAsync($"{Lanes}/{in2.LaneId}/devices/{barrierId}");
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.Single((await Read<AdminResponse<LaneView>>(removed)).Data.Devices);
    }
}
