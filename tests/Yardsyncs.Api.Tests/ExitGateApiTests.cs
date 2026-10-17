using System.Net;
using System.Net.Http.Json;
using Yardsyncs.Api;
using Yardsyncs.Application.Booking;
using Yardsyncs.Application.Gate;
using Yardsyncs.Domain.Gate;
using Yardsyncs.Infrastructure;
using Yardsyncs.Infrastructure.Gate;

namespace Yardsyncs.Api.Tests;

/// <summary>The exit gate: containers seen on the truck checked against the visit; held trucks let out with a note.</summary>
public sealed class ExitGateApiTests(YardsyncsApiFactory factory) : ApiTestBase(factory)
{
    private string Gate => $"{Terminal}/gate";

    /// <summary>An import pickup, checked in (early) and let in from the trouble desk: inside the terminal.</summary>
    private async Task<AppointmentDto> Inside(HttpClient clerk, string container, int truck)
    {
        var dispatcher = await As(DemoDataSeeder.DispatcherEmail);
        var booked = await Read<AppointmentDto>(await dispatcher.PostAsJsonAsync(Appointments, new
        {
            slotId = SlotId, companyId = Demo.CompanyId, truckId = Demo.TruckIds[truck],
            items = new[] { new { category = "IMP_PICKUP", containerNo = container } },
        }));
        var arrival = (await Read<AdminResponse<ArrivalResult>>(await clerk.PostAsJsonAsync($"{Gate}/arrivals", new { laneCode = "IN-1", appointmentNo = booked.AppointmentNo }))).Data;
        Assert.Equal(HttpStatusCode.OK, (await clerk.PostAsJsonAsync($"{Gate}/troubles/{arrival.TroubleId}/resolve",
            new { action = "Admit", note = "Planner agreed", appointmentNo = booked.AppointmentNo })).StatusCode);
        return booked;
    }

    private async Task<(int LaneId, Dictionary<string, HttpClient> Devices)> Out1(HttpClient admin, bool automatic)
    {
        var lane = (await Read<List<LaneView>>(await admin.GetAsync($"{Gate}/lanes"))).Single(l => l.LaneCode == "OUT-1");
        if (automatic)
            Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"{Gate}/lanes/{lane.LaneId}", new { mode = "Automatic" })).StatusCode);
        var clients = new Dictionary<string, HttpClient>();
        foreach (var device in lane.Devices)
        {
            var key = (await Read<AdminResponse<DeviceKeyView>>(await admin.PostAsync($"{Gate}/lanes/{lane.LaneId}/devices/{device.DeviceId}/key", null))).Data.Key;
            var client = Factory.CreateClient();
            client.DefaultRequestHeaders.Add(DeviceEndpoints.KeyHeader, key);
            clients[device.DeviceCode] = client;
        }
        return (lane.LaneId, clients);
    }

    [Fact]
    public async Task A_truck_leaving_without_its_container_is_held_until_a_clerk_lets_it_out()
    {
        if (!Enabled) return;
        var admin = await As(DemoDataSeeder.TerminalAdminEmail);
        var empty = await Inside(admin, "MSCU1234566", 0);
        var loaded = await Inside(admin, "MEDU2601003", 1);
        var (_, devices) = await Out1(admin, automatic: false);

        // The exit camera sees 1234 ABJ leave with nothing on it.
        await devices["OUT1-CAM"].PostAsJsonAsync("/api/devices/reads", new { plate = "1234 ABJ" });
        var held = await admin.PostAsJsonAsync($"{Gate}/out", new { laneCode = "OUT-1", appointmentNo = empty.AppointmentNo });
        Assert.Equal(HttpStatusCode.Conflict, held.StatusCode);
        Assert.Contains("MSCU1234566 should leave on this truck", await held.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"{Gate}/out", new { laneCode = "OUT-1", appointmentNo = empty.AppointmentNo, letOut = true })).StatusCode);

        var letOut = await admin.PostAsJsonAsync($"{Gate}/out", new { laneCode = "OUT-1", appointmentNo = empty.AppointmentNo, letOut = true, note = "Container stays: customs exam, TOS updated" });
        Assert.Equal(HttpStatusCode.OK, letOut.StatusCode);
        Assert.Equal(ExitCheckStatus.Problem, (await Read<AdminResponse<GateOutResult>>(letOut)).Data.ExitCheck!.Status);
        var ticket = await Read<GateTicketView>(await admin.GetAsync($"{Gate}/tickets/{empty.AppointmentNo}"));
        Assert.Equal((ExitCheckStatus.Problem, "Container stays: customs exam, TOS updated"), (ticket.ExitStatus, ticket.ExitNote));

        // 5678 HDR leaves with its container: in order (the rear camera read it separately).
        await devices["OUT1-CAM"].PostAsJsonAsync("/api/devices/reads", new { plate = "5678 HDR" });
        await devices["OUT1-CAM"].PostAsJsonAsync("/api/devices/reads", new { containers = new[] { new { number = "MEDU2601003" } } });
        var fine = await admin.PostAsJsonAsync($"{Gate}/out", new { laneCode = "OUT-1", appointmentNo = loaded.AppointmentNo });
        Assert.Equal(HttpStatusCode.OK, fine.StatusCode);
        Assert.Equal(ExitCheckStatus.Ok, (await Read<AdminResponse<GateOutResult>>(fine)).Data.ExitCheck!.Status);
    }

    [Fact]
    public async Task An_automatic_exit_holds_a_truck_with_the_wrong_container_on_red()
    {
        if (!Enabled) return;
        var admin = await As(DemoDataSeeder.TerminalAdminEmail);
        var booked = await Inside(admin, "MEDU2601003", 1);
        var (laneId, devices) = await Out1(admin, automatic: true);

        await devices["OUT1-CAM"].PostAsJsonAsync("/api/devices/reads", new { plate = "5678 HDR", containers = new[] { new { number = "CSQU3054383" } } });
        var tx = (await Read<List<LaneTransactionView>>(await admin.GetAsync($"{Gate}/transactions?laneId={laneId}"))).Single();
        Assert.Equal((LaneTransactionStatus.Referred, "EXIT_CHECK", booked.AppointmentNo), (tx.Status, tx.Reason, tx.AppointmentNo));
        Assert.Contains("CSQU3054383 is on the truck", tx.Message);
        Assert.Empty(await Read<List<DeviceCommandView>>(await devices["OUT1-BARRIER"].GetAsync("/api/devices/commands")));

        // The clerk checks the camera picture, finds the right container on the truck, and lets it out.
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"{Gate}/transactions/{tx.TransactionId}/let-out", new { note = "" })).StatusCode);
        var letOut = await admin.PostAsJsonAsync($"{Gate}/transactions/{tx.TransactionId}/let-out", new { note = "OCR misread; MEDU2601003 checked on camera" });
        Assert.Equal(HttpStatusCode.OK, letOut.StatusCode);
        Assert.Equal(LaneCommand.Open, Assert.Single(await Read<List<DeviceCommandView>>(await devices["OUT1-BARRIER"].GetAsync("/api/devices/commands"))).Command);
        var board = await Read<GateBoard>(await admin.GetAsync($"{Gate}/board"));
        Assert.DoesNotContain(board.InTerminal, t => t.AppointmentNo == booked.AppointmentNo);
    }
}
