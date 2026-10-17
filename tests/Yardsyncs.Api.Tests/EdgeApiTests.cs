using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yardsyncs.Application.Booking;
using Yardsyncs.Application.Edge;
using Yardsyncs.Application.Gate;
using Yardsyncs.Domain.Booking;
using Yardsyncs.Domain.Gate;
using Yardsyncs.Infrastructure;
using Yardsyncs.Infrastructure.Persistence;

namespace Yardsyncs.Api.Tests;

/// <summary>Edge mode: the terminal's lane agent takes a snapshot and sends back what it decided without the cloud.</summary>
public sealed class EdgeApiTests(YardsyncsApiFactory factory) : ApiTestBase(factory)
{
    private string Gate => $"{Terminal}/gate";

    private async Task<HttpClient> AgentAsync(HttpClient admin)
    {
        var issued = (await Read<AdminResponse<GateSystemKeyIssued>>(await admin.PostAsJsonAsync($"{Gate}/systems", new { name = "Lane agent" }))).Data;
        var agent = Factory.CreateClient();
        agent.DefaultRequestHeaders.Add(GateSystemKeys.Header, issued.Key);
        return agent;
    }

    [Fact]
    public async Task The_lane_agent_takes_a_snapshot_of_what_the_lanes_need()
    {
        if (!Enabled) return;
        var admin = await As(DemoDataSeeder.TerminalAdminEmail);
        var agent = await AgentAsync(admin);
        var dispatcher = await As(DemoDataSeeder.DispatcherEmail);
        var booked = await Read<AppointmentDto>(await dispatcher.PostAsJsonAsync(Appointments, new
        {
            slotId = SlotId, companyId = Demo.CompanyId, truckId = Demo.TruckIds[0],
            items = new[] { new { category = "IMP_PICKUP", containerNo = "MSCU1234566" } },
        }));
        var in1 = (await Read<List<LaneView>>(await admin.GetAsync($"{Gate}/lanes"))).Single(l => l.LaneCode == "IN-1");
        var camera = in1.Devices.Single(d => d.DeviceCode == "IN1-CAM-FRONT");
        var deviceKey = (await Read<AdminResponse<DeviceKeyView>>(await admin.PostAsync($"{Gate}/lanes/{in1.LaneId}/devices/{camera.DeviceId}/key", null))).Data.Key;

        // Only the agent's key: the snapshot holds device fingerprints and gate pass secrets.
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync($"{Gate}/edge/snapshot")).StatusCode);
        var snapshot = await Read<EdgeSnapshot>(await agent.GetAsync($"{Gate}/edge/snapshot"));
        Assert.Equal(Demo.TerminalId, snapshot.TerminalId);
        Assert.Contains(snapshot.Lanes, l => l.LaneCode == "IN-1" && l.Direction == LaneDirection.In);
        Assert.Equal(DeviceKeys.Hash(deviceKey), Assert.Single(snapshot.Devices).KeyHash); // only devices with a key
        var appointment = snapshot.Appointments.Single(a => a.AppointmentNo == booked.AppointmentNo);
        Assert.Equal(("1234ABJ", AppointmentStatus.Confirmed), (appointment.Plate, appointment.Status));
        Assert.Contains("MSCU1234566", appointment.Containers);

        // The same rules decide at the terminal.
        var engine = new EdgeLaneEngine(snapshot, settleSeconds: 0);
        Assert.Equal("IN1-CAM-FRONT", engine.Authenticate(deviceKey)!.DeviceCode);
    }

    [Fact]
    public async Task What_the_agent_decided_offline_is_recorded_once_at_the_time_it_happened()
    {
        if (!Enabled) return;
        var admin = await As(DemoDataSeeder.TerminalAdminEmail);
        var agent = await AgentAsync(admin);
        var dispatcher = await As(DemoDataSeeder.DispatcherEmail);
        var booked = await Read<AppointmentDto>(await dispatcher.PostAsJsonAsync(Appointments, new
        {
            slotId = SlotId, companyId = Demo.CompanyId, truckId = Demo.TruckIds[0],
            items = new[] { new { category = "IMP_PICKUP", containerNo = "MSCU1234566" } },
        }));

        var decided = DateTimeOffset.UtcNow.AddMinutes(-50);
        var entry = new EdgePassage(Guid.NewGuid(), "IN-1", LaneDirection.In, booked.AppointmentNo, "1234ABJ", ["MSCU1234566"],
            decided, true, null, "Admit. Decided at the terminal without the cloud.", decided.AddSeconds(6));
        var exit = new EdgePassage(Guid.NewGuid(), "OUT-1", LaneDirection.Out, booked.AppointmentNo, "1234ABJ", [],
            decided.AddMinutes(35), true, null, "Gated out at the terminal without the cloud (exit not checked).", decided.AddMinutes(35).AddSeconds(5));
        var referred = new EdgePassage(Guid.NewGuid(), "IN-1", LaneDirection.In, null, "9999ZZZ", [],
            decided.AddMinutes(10), false, "NO_APPOINTMENT", "No appointment found for this truck.", null);
        var unknown = new EdgePassage(Guid.NewGuid(), "IN-1", LaneDirection.In, "NOPE-1", null, [], decided, true, null, "Admit", decided);

        var results = await Read<List<EdgePassageResult>>(await agent.PostAsJsonAsync($"{Gate}/edge/passages", new[] { exit, referred, entry, unknown }));
        Assert.Equal(EdgePassageOutcome.Applied, results.Single(r => r.PassageId == entry.PassageId).Outcome);
        Assert.Equal(EdgePassageOutcome.Applied, results.Single(r => r.PassageId == exit.PassageId).Outcome); // oldest first: in, then out
        Assert.Equal(EdgePassageOutcome.Applied, results.Single(r => r.PassageId == referred.PassageId).Outcome);
        Assert.Equal(EdgePassageOutcome.Failed, results.Single(r => r.PassageId == unknown.PassageId).Outcome);

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<YardsyncsDbContext>();
            var visit = await db.Appointments.AsNoTracking().SingleAsync(a => a.AppointmentNo == booked.AppointmentNo);
            Assert.Equal(AppointmentStatus.Completed, visit.Status);
            Assert.Equal(entry.PassedAt!.Value.ToUnixTimeSeconds(), visit.GateInAt!.Value.ToUnixTimeSeconds());
            Assert.Equal(exit.PassedAt!.Value.ToUnixTimeSeconds(), visit.GateOutAt!.Value.ToUnixTimeSeconds());
            Assert.True(await db.GateEvents.AnyAsync(e => e.EventType == GateEventType.Trouble && e.PlateRead == "9999ZZZ"));
        }

        // Sent again (the answer was lost): nothing changes.
        var again = await Read<List<EdgePassageResult>>(await agent.PostAsJsonAsync($"{Gate}/edge/passages", new[] { entry, exit, referred }));
        Assert.All(again, r => Assert.Equal(EdgePassageOutcome.Duplicate, r.Outcome));

        // People can't send passages; only the agent's key.
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsJsonAsync($"{Gate}/edge/passages", new[] { referred })).StatusCode);
    }
}
