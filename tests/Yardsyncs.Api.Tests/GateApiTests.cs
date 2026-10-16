using System.Net;
using System.Net.Http.Json;
using Yardsyncs.Api;
using Yardsyncs.Application.Gate;
using Yardsyncs.Domain.Gate;
using Yardsyncs.Infrastructure;

namespace Yardsyncs.Api.Tests;

/// <summary>Gate endpoints and who may use them (the gate logic is covered by the database tests).</summary>
public sealed class GateApiTests(YardsyncsApiFactory factory) : ApiTestBase(factory)
{
    private string Gate => $"{Terminal}/gate";

    [Fact]
    public async Task Only_terminal_staff_work_the_gate()
    {
        if (!Enabled) return;

        var dispatcher = await As(DemoDataSeeder.DispatcherEmail);
        var planner = await As(DemoDataSeeder.PlannerEmail);
        var admin = await As(DemoDataSeeder.TerminalAdminEmail);

        Assert.Equal(HttpStatusCode.Forbidden, (await dispatcher.PostAsJsonAsync($"{Gate}/arrivals", new { laneCode = "IN-1", plateRead = "1234 ABJ" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await dispatcher.GetAsync($"{Gate}/board")).StatusCode);

        // An unknown truck goes to the trouble desk.
        var arrival = await planner.PostAsJsonAsync($"{Gate}/arrivals", new { laneCode = "IN-1", plateRead = "9999 ZZZ" });
        Assert.Equal(HttpStatusCode.OK, arrival.StatusCode);
        var result = (await Read<AdminResponse<ArrivalResult>>(arrival)).Data;
        Assert.Equal(GateDecisionKind.Refer, result.Decision);
        Assert.Equal(TroubleReason.NoAppointment, result.Reason);

        var troubles = await Read<List<TroubleView>>(await planner.GetAsync($"{Gate}/troubles"));
        Assert.Equal(result.TroubleId, Assert.Single(troubles).TroubleId);

        var board = await Read<GateBoard>(await planner.GetAsync($"{Gate}/board"));
        Assert.Equal(1, board.OpenTroubles);

        var dismissed = await planner.PostAsJsonAsync($"{Gate}/troubles/{result.TroubleId}/resolve", new { action = "Dismiss", note = "Truck turned around" });
        Assert.Equal(HttpStatusCode.OK, dismissed.StatusCode);

        // Lanes: everyone at the gate can list, only terminal admins add.
        Assert.Equal(3, (await Read<List<LaneView>>(await planner.GetAsync($"{Gate}/lanes"))).Count);
        Assert.Equal(HttpStatusCode.Forbidden, (await planner.PostAsJsonAsync($"{Gate}/lanes", new { gateCode = "NORTH", laneCode = "IN-3", direction = "In" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync($"{Gate}/lanes", new { gateCode = "NORTH", laneCode = "IN-3", direction = "In" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"{Gate}/lanes", new { gateCode = "NORTH", laneCode = "in-3", direction = "In" })).StatusCode);
    }
}
