using System.Net;
using System.Net.Http.Json;
using Tas.Application.Booking;
using Tas.Application.Gate;
using Tas.Domain.Gate;
using Tas.Infrastructure;

namespace Tas.Api.Tests;

/// <summary>Per-terminal gate rules: how the gate finds appointments and how strictly it identifies drivers.</summary>
public sealed class GateSettingsApiTests(TasApiFactory factory) : ApiTestBase(factory)
{
    private string Gate => $"{Terminal}/gate";

    [Fact]
    public async Task A_terminal_can_require_a_named_driver_and_turn_off_plate_matching()
    {
        if (!Enabled) return;

        var admin = await As(DemoDataSeeder.TerminalAdminEmail);
        var settings = $"{Terminal}/settings";
        Assert.Equal(1, (await Read<Dictionary<string, int>>(await admin.GetAsync(settings)))["gate_by_plate"]);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync(settings, new { values = new { driver_identity = 3 } })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync(settings, new { values = new { driver_identity = 1, gate_by_plate = 0 } })).StatusCode);
        var saved = await Read<Dictionary<string, int>>(await admin.GetAsync(settings));
        Assert.Equal((1, 0, 1), (saved["driver_identity"], saved["gate_by_plate"], saved["gate_by_number"]));

        // A booking without a driver: found by its number, then referred because the terminal wants a named driver.
        var dispatcher = await As(DemoDataSeeder.DispatcherEmail);
        var booked = await Read<AppointmentDto>(await dispatcher.PostAsJsonAsync(Appointments, new
        {
            slotId = SlotId, companyId = Demo.CompanyId, truckId = Demo.TruckIds[0],
            items = new[] { new { category = "IMP_PICKUP", containerNo = "MSCU1234566" } },
        }));
        var arrival = (await Read<AdminResponse<ArrivalResult>>(await admin.PostAsJsonAsync($"{Gate}/arrivals", new { laneCode = "IN-1", appointmentNo = booked.AppointmentNo }))).Data;
        Assert.Equal(GateDecisionKind.Refer, arrival.Decision);
        Assert.Equal(TroubleReason.DriverUnknown, arrival.Reason);
        Assert.Contains("names no driver", arrival.Message);

        // The truck's plate would find it; this terminal turned that off.
        var byPlate = (await Read<AdminResponse<ArrivalResult>>(await admin.PostAsJsonAsync($"{Gate}/arrivals", new { laneCode = "IN-1", plateRead = "1234 ABJ" }))).Data;
        Assert.Equal(MatchMethod.None, byPlate.MatchedBy);
        Assert.Equal(TroubleReason.NoAppointment, byPlate.Reason);
    }
}
