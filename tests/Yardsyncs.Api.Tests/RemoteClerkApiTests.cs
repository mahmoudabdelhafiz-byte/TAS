using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yardsyncs.Api;
using Yardsyncs.Application.Booking;
using Yardsyncs.Application.Gate;
using Yardsyncs.Domain.Gate;
using Yardsyncs.Infrastructure;
using Yardsyncs.Infrastructure.Persistence;

namespace Yardsyncs.Api.Tests;

/// <summary>The remote clerk station: a truck's detail, correcting misreads and checking again, the intercom.</summary>
public sealed class RemoteClerkApiTests(YardsyncsApiFactory factory) : ApiTestBase(factory)
{
    private string Gate => $"{Terminal}/gate";

    private async Task<(int LaneId, Dictionary<string, HttpClient> Devices)> AutomaticIn1(HttpClient admin)
    {
        var lane = (await Read<List<LaneView>>(await admin.GetAsync($"{Gate}/lanes"))).Single(l => l.LaneCode == "IN-1");
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

    private async Task<AppointmentDto> BookTruck1234(bool now)
    {
        var dispatcher = await As(DemoDataSeeder.DispatcherEmail);
        var booked = await Read<AppointmentDto>(await dispatcher.PostAsJsonAsync(Appointments, new
        {
            slotId = SlotId, companyId = Demo.CompanyId, truckId = Demo.TruckIds[0],
            items = new[] { new { category = "IMP_PICKUP", containerNo = "MSCU1234566" } },
        }));
        if (now)
        {
            using var scope = Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<YardsyncsDbContext>();
            var t = DateTimeOffset.UtcNow;
            await db.Slots.Where(s => s.SlotId == SlotId).ExecuteUpdateAsync(s => s.SetProperty(x => x.StartAt, t.AddMinutes(-10)).SetProperty(x => x.EndAt, t.AddMinutes(50)));
        }
        return booked;
    }

    private async Task<LaneTransactionView> Latest(HttpClient client, int laneId) =>
        (await Read<List<LaneTransactionView>>(await client.GetAsync($"{Gate}/transactions?laneId={laneId}&limit=1"))).Single();

    [Fact]
    public async Task The_clerk_corrects_a_misread_plate_and_the_lane_lets_the_truck_in()
    {
        if (!Enabled) return;
        var admin = await As(DemoDataSeeder.TerminalAdminEmail);
        var planner = await As(DemoDataSeeder.PlannerEmail);
        var booked = await BookTruck1234(now: true);
        var (laneId, devices) = await AutomaticIn1(admin);

        // The camera misreads J as X: no appointment, the truck waits.
        await devices["IN1-CAM-FRONT"].PostAsJsonAsync("/api/devices/reads", new { plate = "1234 ABX", plateConfidence = 58, photos = new[] { "https://ocr.example/1.jpg" } });
        var referred = await Latest(planner, laneId);
        Assert.Equal((LaneTransactionStatus.Referred, "NO_APPOINTMENT"), (referred.Status, referred.Reason));

        var detail = await Read<LaneTransactionDetail>(await planner.GetAsync($"{Gate}/transactions/{referred.TransactionId}"));
        Assert.Equal("https://ocr.example/1.jpg", Assert.Single(Assert.Single(detail.Reads).Photos));
        Assert.Equal(TroubleReason.NoAppointment, detail.Trouble!.Reason);
        Assert.Contains(detail.Commands, c => c.Command == LaneCommand.Red);
        Assert.Equal(5, detail.Devices.Count);

        // Company users can't.
        var dispatcher = await As(DemoDataSeeder.DispatcherEmail);
        Assert.Equal(HttpStatusCode.Forbidden, (await dispatcher.PostAsJsonAsync($"{Gate}/transactions/{referred.TransactionId}/recheck", new { plate = "1234 ABJ" })).StatusCode);

        var rechecked = await planner.PostAsJsonAsync($"{Gate}/transactions/{referred.TransactionId}/recheck", new { plate = "1234 ABJ" });
        Assert.Equal(HttpStatusCode.OK, rechecked.StatusCode);
        var admitted = (await Read<AdminResponse<LaneTransactionView>>(rechecked)).Data;
        Assert.Equal((LaneTransactionStatus.Admitted, booked.AppointmentNo, true), (admitted.Status, admitted.AppointmentNo, admitted.Corrected));
        Assert.Empty(await Read<List<TroubleView>>(await planner.GetAsync($"{Gate}/troubles")));
        Assert.Contains((await Read<List<DeviceCommandView>>(await devices["IN1-BARRIER"].GetAsync("/api/devices/commands"))), c => c.Command == LaneCommand.Open);

        // Decided: nothing more to check.
        Assert.Equal(HttpStatusCode.Conflict, (await planner.PostAsJsonAsync($"{Gate}/transactions/{referred.TransactionId}/recheck", new { plate = "1234 ABJ" })).StatusCode);
    }

    [Fact]
    public async Task The_clerk_talks_to_the_driver_and_types_the_appointment_number()
    {
        if (!Enabled) return;
        var admin = await As(DemoDataSeeder.TerminalAdminEmail);
        var booked = await BookTruck1234(now: false); // tomorrow
        var (laneId, devices) = await AutomaticIn1(admin);

        await devices["IN1-CAM-FRONT"].PostAsJsonAsync("/api/devices/reads", new { plate = "9999 ZZZ" });
        var referred = await Latest(admin, laneId);

        // No intercom at this lane yet.
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"{Gate}/lanes/{laneId}/commands", new { command = "Call" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync($"{Gate}/lanes/{laneId}/devices", new { deviceCode = "IN1-INTERCOM", kind = "Intercom" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync($"{Gate}/lanes/{laneId}/commands", new { command = "Call" })).StatusCode);

        // A container number that fails its check digit is refused.
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"{Gate}/transactions/{referred.TransactionId}/recheck", new { containers = new[] { "MSCU1234567" } })).StatusCode);

        // The driver gives the appointment number over the intercom: found, but early — a fresh case.
        var again = (await Read<AdminResponse<LaneTransactionView>>(await admin.PostAsJsonAsync(
            $"{Gate}/transactions/{referred.TransactionId}/recheck", new { appointmentNo = booked.AppointmentNo.ToLowerInvariant() }))).Data;
        Assert.Equal((LaneTransactionStatus.Referred, "EARLY", booked.AppointmentNo), (again.Status, again.Reason, again.AppointmentNo));
        Assert.NotEqual(referred.TroubleId, again.TroubleId);
        var open = Assert.Single(await Read<List<TroubleView>>(await admin.GetAsync($"{Gate}/troubles")));
        Assert.Equal(again.TroubleId, open.TroubleId);
    }
}
