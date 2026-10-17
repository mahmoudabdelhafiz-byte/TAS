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

/// <summary>The lane controller at an automatic lane: a camera read decides, the barrier opens, its confirmation gates in.</summary>
public sealed class LaneControllerApiTests(YardsyncsApiFactory factory) : ApiTestBase(factory)
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

    private async Task<AppointmentDto> BookTruck1234()
    {
        var dispatcher = await As(DemoDataSeeder.DispatcherEmail);
        var booked = await dispatcher.PostAsJsonAsync(Appointments, new
        {
            slotId = SlotId, companyId = Demo.CompanyId, truckId = Demo.TruckIds[0],
            items = new[] { new { category = "IMP_PICKUP", containerNo = "MSCU1234566" } },
        });
        Assert.Equal(HttpStatusCode.Created, booked.StatusCode);
        return await Read<AppointmentDto>(booked);
    }

    /// <summary>Moves the booked slot to now, so the truck is on time.</summary>
    private async Task SlotIsNow()
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<YardsyncsDbContext>();
        var now = DateTimeOffset.UtcNow;
        await db.Slots.Where(s => s.SlotId == SlotId).ExecuteUpdateAsync(s => s.SetProperty(x => x.StartAt, now.AddMinutes(-10)).SetProperty(x => x.EndAt, now.AddMinutes(50)));
    }

    private static async Task<List<DeviceCommandView>> Commands(HttpClient device) => await Read<List<DeviceCommandView>>(await device.GetAsync("/api/devices/commands"));

    [Fact]
    public async Task A_truck_on_time_is_let_in_and_the_barrier_gates_it_in()
    {
        if (!Enabled) return;
        var admin = await As(DemoDataSeeder.TerminalAdminEmail);
        var booked = await BookTruck1234();
        await SlotIsNow();
        var (laneId, devices) = await AutomaticIn1(admin);

        Assert.Equal(HttpStatusCode.Created, (await devices["IN1-CAM-FRONT"].PostAsJsonAsync("/api/devices/reads", new { plate = "1234 ABJ", plateConfidence = 92 })).StatusCode);

        var tx = Assert.Single(await Read<List<LaneTransactionView>>(await admin.GetAsync($"{Gate}/transactions?laneId={laneId}")));
        Assert.Equal((LaneTransactionStatus.Admitted, booked.AppointmentNo, "1234ABJ"), (tx.Status, tx.AppointmentNo, tx.Plate));

        // Green, open, a welcome on the kiosk, no printer at this lane.
        Assert.Equal(LaneCommand.Green, Assert.Single(await Commands(devices["IN1-LIGHT"])).Command);
        Assert.Contains("go ahead", Assert.Single(await Commands(devices["IN1-KIOSK"])).Text);
        var open = Assert.Single(await Commands(devices["IN1-BARRIER"]));
        Assert.Equal(LaneCommand.Open, open.Command);

        // The rear camera reads the container a moment later: same truck, no second decision.
        await devices["IN1-OCR"].PostAsJsonAsync("/api/devices/reads", new
        {
            containers = new[] { new { number = "MSCU1234566" } }, seals = new[] { "sl 12345" },
            damage = new[] { new { photo = "https://ocr.example/d1.jpg", note = "Dent", side = "rear" } },
        });
        var same = Assert.Single(await Read<List<LaneTransactionView>>(await admin.GetAsync($"{Gate}/transactions?laneId={laneId}")));
        Assert.Equal((2, "MSCU1234566"), (same.ReadCount, same.Containers.Single()));

        // The barrier opened: the truck is in.
        await devices["IN1-BARRIER"].PostAsJsonAsync($"/api/devices/commands/{open.CommandId}/ack", new { done = true });
        Assert.Equal(LaneTransactionStatus.Passed, Assert.Single(await Read<List<LaneTransactionView>>(await admin.GetAsync($"{Gate}/transactions"))).Status);
        var board = await Read<GateBoard>(await admin.GetAsync($"{Gate}/board"));
        Assert.Contains(board.InTerminal, t => t.AppointmentNo == booked.AppointmentNo);
        var lane = (await Read<List<LaneView>>(await admin.GetAsync($"{Gate}/lanes"))).Single(l => l.LaneId == laneId);
        Assert.Equal(LaneTransactionStatus.Passed, lane.Transaction!.Status);

        // What the cameras saw became the gate-in inspection.
        var inspected = Assert.Single((await Read<AppointmentInspections>(await admin.GetAsync($"{Appointments}/{booked.AppointmentNo}/inspections"))).Inspections);
        Assert.Equal((InspectionSource.Camera, "SL12345", SealStatus.Ok, InspectionStage.In), (inspected.Source, inspected.SealSeen, inspected.SealStatus, inspected.Stage));
        Assert.Equal(DamageSide.Rear, Assert.Single(inspected.Damage).Side);
    }

    [Fact]
    public async Task The_driver_shows_the_gate_pass_at_the_kiosk_and_gets_the_yard_position_at_gate_in()
    {
        if (!Enabled) return;
        var admin = await As(DemoDataSeeder.TerminalAdminEmail);
        var dispatcher = await As(DemoDataSeeder.DispatcherEmail);
        var booked = await Read<AppointmentDto>(await dispatcher.PostAsJsonAsync(Appointments, new
        {
            slotId = SlotId, companyId = Demo.CompanyId, truckId = Demo.TruckIds[0], driverId = Demo.DriverIds[0],
            items = new[] { new { category = "IMP_PICKUP", containerNo = "MSCU1234566" } },
        }));
        await SlotIsNow();
        var in1 = (await Read<List<LaneView>>(await admin.GetAsync($"{Gate}/lanes"))).Single(l => l.LaneCode == "IN-1");
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync($"{Gate}/lanes/{in1.LaneId}/devices", new { deviceCode = "IN1-PRINTER", kind = "Printer" })).StatusCode);
        var (laneId, devices) = await AutomaticIn1(admin);

        // The driver's app: sign in with the code, get the rotating gate pass.
        const string mobile = "0500000001";
        var start = await Factory.CreateClient().PostAsJsonAsync("/api/driver/login/start", new { mobile, installId = "kiosk-test-phone" });
        using var started = System.Text.Json.JsonDocument.Parse(await start.Content.ReadAsStringAsync());
        var verify = await Factory.CreateClient().PostAsJsonAsync("/api/driver/login/verify",
            new { mobile, installId = "kiosk-test-phone", code = started.RootElement.GetProperty("testCode").GetString(), platform = "android", appVersion = "0.1.0" });
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
        var phone = Factory.CreateClient();
        phone.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer",
            (await Read<Yardsyncs.Infrastructure.DriverApp.DriverSession>(verify)).Token);
        var pass = await Read<Yardsyncs.Infrastructure.DriverApp.GatePassView>(await phone.GetAsync("/api/driver/gate-pass"));
        var code = GatePassCode.Payload(pass.PassId, booked.AppointmentNo,
            GatePassCode.Compute(Convert.FromBase64String(pass.Secret), GatePassCode.Step(DateTimeOffset.UtcNow)));

        // The kiosk scans it: decided at once, let in.
        Assert.Equal(HttpStatusCode.Created, (await devices["IN1-KIOSK"].PostAsJsonAsync("/api/devices/reads", new { qrToken = code })).StatusCode);
        var tx = Assert.Single(await Read<List<LaneTransactionView>>(await admin.GetAsync($"{Gate}/transactions?laneId={laneId}")));
        Assert.Equal((LaneTransactionStatus.Admitted, booked.AppointmentNo), (tx.Status, tx.AppointmentNo)); // the pass itself is not kept once decided
        Assert.Empty(await Commands(devices["IN1-PRINTER"])); // the ticket waits for gate-in
        var welcome = Assert.Single(await Commands(devices["IN1-KIOSK"]));
        await devices["IN1-KIOSK"].PostAsJsonAsync($"/api/devices/commands/{welcome.CommandId}/ack", new { done = true });

        // The barrier opened: in, and the TOS's yard position goes to the kiosk and onto the printed ticket.
        var open = Assert.Single(await Commands(devices["IN1-BARRIER"]));
        await devices["IN1-BARRIER"].PostAsJsonAsync($"/api/devices/commands/{open.CommandId}/ack", new { done = true });
        Assert.Equal("Go to B2 (14.03.2)", Assert.Single(await Commands(devices["IN1-KIOSK"])).Text);
        var ticket = Assert.Single(await Commands(devices["IN1-PRINTER"]));
        Assert.Equal(LaneCommand.Print, ticket.Command);
        Assert.Contains($"Appointment: {booked.AppointmentNo}", ticket.Text);
        Assert.EndsWith("Go to: B2 (14.03.2)", ticket.Text);

        var view = await Read<GateTicketView>(await admin.GetAsync($"{Gate}/tickets/{booked.AppointmentNo}"));
        Assert.Equal("B2 (14.03.2)", view.YardPosition);
    }

    [Fact]
    public async Task A_seal_that_isnt_the_declared_one_holds_the_truck_where_the_terminal_asks_for_it()
    {
        if (!Enabled) return;
        var admin = await As(DemoDataSeeder.TerminalAdminEmail);
        var dispatcher = await As(DemoDataSeeder.DispatcherEmail);
        var booked = await BookTruck1234();
        await SlotIsNow();
        Assert.Equal(HttpStatusCode.OK, (await dispatcher.PutAsJsonAsync($"{Appointments}/{booked.AppointmentNo}/items/1/seal", new { sealNo = "ML-449 120" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"{Terminal}/settings", new { values = new { seal_hold = 1 } })).StatusCode);
        var (laneId, devices) = await AutomaticIn1(admin);

        // The rear camera reads the container and another seal: red, and the case on the trouble desk.
        await devices["IN1-OCR"].PostAsJsonAsync("/api/devices/reads", new { containers = new[] { new { number = "MSCU1234566" } }, seals = new[] { "zz 0001" } });
        var tx = Assert.Single(await Read<List<LaneTransactionView>>(await admin.GetAsync($"{Gate}/transactions?laneId={laneId}")));
        Assert.Equal((LaneTransactionStatus.Referred, "SEAL_MISMATCH"), (tx.Status, tx.Reason));
        Assert.Contains("declared ML449120, read ZZ0001", tx.Message);
        Assert.Empty(await Commands(devices["IN1-BARRIER"]));
        var trouble = Assert.Single(await Read<List<TroubleView>>(await admin.GetAsync($"{Gate}/troubles")));
        Assert.Equal(TroubleReason.SealMismatch, trouble.Reason);

        // The clerk checks the seal and lets the truck in: the barrier opens.
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"{Gate}/troubles/{tx.TroubleId}/resolve",
            new { action = "Admit", note = "Seal replaced by customs, papers checked", appointmentNo = booked.AppointmentNo })).StatusCode);
        Assert.Equal(LaneCommand.Open, Assert.Single(await Commands(devices["IN1-BARRIER"])).Command);
    }

    [Fact]
    public async Task Without_the_seal_hold_a_seal_mismatch_is_only_recorded()
    {
        if (!Enabled) return;
        var admin = await As(DemoDataSeeder.TerminalAdminEmail);
        var dispatcher = await As(DemoDataSeeder.DispatcherEmail);
        var booked = await BookTruck1234();
        await SlotIsNow();
        Assert.Equal(HttpStatusCode.OK, (await dispatcher.PutAsJsonAsync($"{Appointments}/{booked.AppointmentNo}/items/1/seal", new { sealNo = "ML-449 120" })).StatusCode);
        var (laneId, devices) = await AutomaticIn1(admin);

        await devices["IN1-OCR"].PostAsJsonAsync("/api/devices/reads", new { containers = new[] { new { number = "MSCU1234566" } }, seals = new[] { "zz 0001" } });
        Assert.Equal(LaneTransactionStatus.Admitted, Assert.Single(await Read<List<LaneTransactionView>>(await admin.GetAsync($"{Gate}/transactions?laneId={laneId}"))).Status);
    }

    [Fact]
    public async Task A_truck_that_is_early_waits_on_red_until_a_clerk_lets_it_in()
    {
        if (!Enabled) return;
        var admin = await As(DemoDataSeeder.TerminalAdminEmail);
        var booked = await BookTruck1234(); // tomorrow
        var (laneId, devices) = await AutomaticIn1(admin);

        // The driver types the appointment number at the kiosk (a plate only finds bookings within a day): decided at once.
        Assert.Equal(HttpStatusCode.Created, (await devices["IN1-KIOSK"].PostAsJsonAsync("/api/devices/reads", new { appointmentNo = booked.AppointmentNo })).StatusCode);
        var tx = Assert.Single(await Read<List<LaneTransactionView>>(await admin.GetAsync($"{Gate}/transactions?laneId={laneId}")));
        Assert.Equal((LaneTransactionStatus.Referred, "EARLY"), (tx.Status, tx.Reason));
        Assert.NotNull(tx.TroubleId);
        Assert.Equal(LaneCommand.Red, Assert.Single(await Commands(devices["IN1-LIGHT"])).Command);
        Assert.StartsWith("Please wait", Assert.Single(await Commands(devices["IN1-KIOSK"])).Text);
        Assert.Empty(await Commands(devices["IN1-BARRIER"]));

        // The clerk lets it in from the trouble desk: the lane's barrier opens.
        var resolved = await admin.PostAsJsonAsync($"{Gate}/troubles/{tx.TroubleId}/resolve", new { action = "Admit", note = "Ship leaving early, planner agreed", appointmentNo = booked.AppointmentNo });
        Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);
        Assert.Equal(LaneCommand.Open, Assert.Single(await Commands(devices["IN1-BARRIER"])).Command);
        Assert.Equal(LaneTransactionStatus.Admitted, Assert.Single(await Read<List<LaneTransactionView>>(await admin.GetAsync($"{Gate}/transactions"))).Status);
    }

    [Fact]
    public async Task A_manual_lane_or_a_closed_one_is_left_to_the_clerk()
    {
        if (!Enabled) return;
        var admin = await As(DemoDataSeeder.TerminalAdminEmail);
        var (laneId, devices) = await AutomaticIn1(admin);

        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"{Gate}/lanes/{laneId}/state", new { state = "Closed", note = "Shift change" })).StatusCode);
        await devices["IN1-CAM-FRONT"].PostAsJsonAsync("/api/devices/reads", new { plate = "9999 ZZZ" });
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"{Gate}/lanes/{laneId}/state", new { state = "Open" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"{Gate}/lanes/{laneId}", new { mode = "Manual" })).StatusCode);
        await devices["IN1-CAM-FRONT"].PostAsJsonAsync("/api/devices/reads", new { plate = "9999 ZZZ" });

        Assert.Empty(await Read<List<LaneTransactionView>>(await admin.GetAsync($"{Gate}/transactions")));
        Assert.Empty(await Read<List<object>>(await admin.GetAsync($"{Gate}/troubles")));
        Assert.Empty(await Commands(devices["IN1-LIGHT"]));
    }
}
