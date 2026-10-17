using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yardsyncs.Api;
using Yardsyncs.Application.Gate;
using Yardsyncs.Domain;
using Yardsyncs.Domain.Gate;
using Yardsyncs.Infrastructure;
using Yardsyncs.Infrastructure.Persistence;

namespace Yardsyncs.Api.Tests;

/// <summary>The truck appointment system without Autogate: an installation without the module.</summary>
public sealed class TasOnlyFactory : YardsyncsApiFactory
{
    protected override void Extra(IWebHostBuilder builder) => builder.UseSetting("Modules:Autogate", "false");
}

/// <summary>An installation sold as the truck appointment system alone: the gate API stays for the terminal's own gate system.</summary>
public sealed class TasOnlyApiTests(TasOnlyFactory factory) : ApiTestBase(factory), IClassFixture<TasOnlyFactory>
{
    private string Gate => $"{Terminal}/gate";

    [Fact]
    public async Task Without_Autogate_the_gate_api_works_and_Autogate_is_not_there()
    {
        if (!Enabled) return;
        var admin = await As(DemoDataSeeder.TerminalAdminEmail);

        var modules = await Read<JsonElement>(await admin.GetAsync($"/api/modules?terminalId={Demo.TerminalId}"));
        Assert.False(modules.GetProperty("autogate").GetBoolean());
        Assert.False((await Read<JsonElement>(await admin.GetAsync("/api/terminals")))[0].GetProperty("autogate").GetBoolean());

        // Autogate's endpoints are not there.
        Assert.Equal(HttpStatusCode.NotFound, (await Factory.CreateClient().GetAsync("/api/devices/me")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"{Terminal}/inspections")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"{Gate}/transactions")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"{Gate}/lane-kpis?from=2026-10-01&to=2026-10-07")).StatusCode);

        // Lanes are plain lanes; none can work automatically.
        var in1 = (await Read<List<LaneView>>(await admin.GetAsync($"{Gate}/lanes"))).Single(l => l.LaneCode == "IN-1");
        Assert.Empty(in1.Devices);
        var automatic = await admin.PutAsJsonAsync($"{Gate}/lanes/{in1.LaneId}", new { mode = "Automatic" });
        Assert.Equal(HttpStatusCode.BadRequest, automatic.StatusCode);
        Assert.Contains("Autogate", await automatic.Content.ReadAsStringAsync());

        // The terminal's own gate system: arrival, gate-in, gate-out, as before.
        var dispatcher = await As(DemoDataSeeder.DispatcherEmail);
        var booked = await Read<Yardsyncs.Application.Booking.AppointmentDto>(await dispatcher.PostAsJsonAsync(Appointments, new
        {
            slotId = SlotId, companyId = Demo.CompanyId, truckId = Demo.TruckIds[0],
            items = new[] { new { category = "IMP_PICKUP", containerNo = "MSCU1234566" } },
        }));
        var arrival = (await Read<AdminResponse<ArrivalResult>>(await admin.PostAsJsonAsync($"{Gate}/arrivals", new { laneCode = "IN-1", appointmentNo = booked.AppointmentNo }))).Data;
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"{Gate}/troubles/{arrival.TroubleId}/resolve",
            new { action = "Admit", note = "Planner agreed", appointmentNo = booked.AppointmentNo })).StatusCode);
        var gateOut = await admin.PostAsJsonAsync($"{Gate}/out", new { laneCode = "OUT-1", appointmentNo = booked.AppointmentNo, containerReads = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.OK, gateOut.StatusCode);
        Assert.Equal(ExitCheckStatus.NotChecked, (await Read<AdminResponse<GateOutResult>>(gateOut)).Data.ExitCheck!.Status);
    }

    [Fact]
    public async Task The_terminals_own_gate_system_works_the_gate_with_its_key()
    {
        if (!Enabled) return;
        var admin = await As(DemoDataSeeder.TerminalAdminEmail);

        // Only terminal admins make keys (not planners); a name is needed.
        var planner = await As(DemoDataSeeder.PlannerEmail);
        Assert.Equal(HttpStatusCode.Forbidden, (await planner.PostAsJsonAsync($"{Gate}/systems", new { name = "North gate OCR" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"{Gate}/systems", new { name = " " })).StatusCode);
        var created = await admin.PostAsJsonAsync($"{Gate}/systems", new { name = "North gate OCR" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var issued = (await Read<AdminResponse<GateSystemKeyIssued>>(created)).Data;
        Assert.StartsWith(GateSystemKeys.Prefix, issued.Key);
        Assert.EndsWith(issued.Key[^6..], issued.System.KeyHint);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"{Gate}/systems", new { name = "North gate OCR" })).StatusCode);

        var gateSystem = Factory.CreateClient();
        gateSystem.DefaultRequestHeaders.Add(GateSystemKeys.Header, issued.Key);
        var wrong = Factory.CreateClient();
        wrong.DefaultRequestHeaders.Add(GateSystemKeys.Header, GateSystemKeys.New());
        Assert.Equal(HttpStatusCode.Unauthorized, (await wrong.GetAsync($"{Gate}/board")).StatusCode);

        // A gate clerk of its own terminal: not an admin, and can't sign in.
        Assert.Equal(HttpStatusCode.Forbidden, (await gateSystem.GetAsync($"{Gate}/systems")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await gateSystem.GetAsync($"/api/terminals/{Demo.TerminalId + 999}/gate/board")).StatusCode);

        var dispatcher = await As(DemoDataSeeder.DispatcherEmail);
        var booked = await Read<Yardsyncs.Application.Booking.AppointmentDto>(await dispatcher.PostAsJsonAsync(Appointments, new
        {
            slotId = SlotId, companyId = Demo.CompanyId, truckId = Demo.TruckIds[0],
            items = new[] { new { category = "IMP_PICKUP", containerNo = "MSCU1234566" } },
        }));

        // Asking first records nothing.
        var troublesBefore = (await Read<List<JsonElement>>(await admin.GetAsync($"{Gate}/troubles"))).Count;
        var check = await gateSystem.PostAsJsonAsync($"{Gate}/arrivals/check", new { laneCode = "IN-1", appointmentNo = booked.AppointmentNo });
        Assert.Equal(HttpStatusCode.OK, check.StatusCode);
        var asked = (await Read<AdminResponse<ArrivalResult>>(check)).Data;
        Assert.Equal(booked.AppointmentNo, asked.AppointmentNo);
        Assert.Null(asked.TroubleId);
        Assert.Equal(troublesBefore, (await Read<List<JsonElement>>(await admin.GetAsync($"{Gate}/troubles"))).Count);

        // Recorded: the same decision, now with its trouble-desk case (the slot is tomorrow: early).
        var arrival = (await Read<AdminResponse<ArrivalResult>>(await gateSystem.PostAsJsonAsync($"{Gate}/arrivals", new { laneCode = "IN-1", appointmentNo = booked.AppointmentNo }))).Data;
        Assert.Equal(asked.Decision, arrival.Decision);
        Assert.Equal(asked.Reason, arrival.Reason);
        Assert.NotNull(arrival.TroubleId);
        Assert.Equal(troublesBefore + 1, (await Read<List<JsonElement>>(await admin.GetAsync($"{Gate}/troubles"))).Count);

        // The admin sees it was used; revoking stops it at once.
        var listed = (await Read<List<GateSystemView>>(await admin.GetAsync($"{Gate}/systems"))).Single();
        Assert.NotNull(listed.LastUsedAt);
        Assert.Equal(HttpStatusCode.OK, (await admin.DeleteAsync($"{Gate}/systems/{listed.KeyId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await gateSystem.GetAsync($"{Gate}/board")).StatusCode);
        Assert.NotNull((await Read<List<GateSystemView>>(await admin.GetAsync($"{Gate}/systems"))).Single().RevokedAt);
    }
}

/// <summary>Autogate installed, but one terminal doesn't have it.</summary>
public sealed class AutogatePerTerminalApiTests(YardsyncsApiFactory factory) : ApiTestBase(factory)
{
    private string Gate => $"{Terminal}/gate";

    [Fact]
    public async Task A_terminal_without_Autogate_has_none_of_it_and_only_system_admins_switch_it()
    {
        if (!Enabled) return;
        var admin = await As(DemoDataSeeder.TerminalAdminEmail);
        var in1 = (await Read<List<LaneView>>(await admin.GetAsync($"{Gate}/lanes"))).Single(l => l.LaneCode == "IN-1");
        Assert.NotEmpty(in1.Devices);
        var camera = in1.Devices.Single(d => d.DeviceCode == "IN1-CAM-FRONT");
        var key = (await Read<AdminResponse<DeviceKeyView>>(await admin.PostAsync($"{Gate}/lanes/{in1.LaneId}/devices/{camera.DeviceId}/key", null))).Data.Key;
        var device = Factory.CreateClient();
        device.DefaultRequestHeaders.Add(DeviceEndpoints.KeyHeader, key);
        Assert.Equal(HttpStatusCode.OK, (await device.GetAsync("/api/devices/me")).StatusCode);

        // What the customer bought is set by system admins, not the terminal's own.
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PutAsJsonAsync($"{Terminal}/settings", new { values = new { module_autogate = 0 } })).StatusCode);
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<YardsyncsDbContext>();
            db.TerminalSettings.Add(new TerminalSetting { TerminalId = Demo.TerminalId, SettingKey = TerminalSettings.ModuleAutogateKey, SettingValue = "0" });
            await db.SaveChangesAsync();
        }

        Assert.False((await Read<JsonElement>(await admin.GetAsync($"/api/modules?terminalId={Demo.TerminalId}"))).GetProperty("autogateAtTerminal").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"{Gate}/lanes/{in1.LaneId}/activity")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await device.GetAsync("/api/devices/me")).StatusCode);
        Assert.Empty((await Read<List<LaneView>>(await admin.GetAsync($"{Gate}/lanes"))).Single(l => l.LaneCode == "IN-1").Devices);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"{Gate}/lanes/{in1.LaneId}", new { mode = "Automatic" })).StatusCode);
        // Saving the settings as they are (module unchanged) is still fine for the terminal admin.
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"{Terminal}/settings", new { values = new { module_autogate = 0, early_grace_min = 30 } })).StatusCode);
    }
}
