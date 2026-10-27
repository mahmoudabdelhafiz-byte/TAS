using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Yardsyncs.Infrastructure;

namespace Yardsyncs.Api.Tests;

/// <summary>RoRo: car carriers booked by VIN, drive-away visits without a truck, one live visit per vehicle.</summary>
public sealed class RoroApiTests(YardsyncsApiFactory factory) : ApiTestBase(factory)
{
    private static readonly string[] Vins = ["JTDBR32E730012457", "KMHCT41BAEU123458", "MALA841CAFM123455"];

    [Fact]
    public async Task A_car_carrier_books_vehicles_by_vin_and_a_drive_away_visit_has_no_truck()
    {
        if (!Enabled) return;

        var admin = await As(DemoDataSeeder.TerminalAdminEmail);
        var categories = $"{Terminal}/categories";
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync(categories,
            new { code = "RORO_PICKUP", nameEn = "Vehicle pickup", nameAr = "استلام مركبات", mode = "Vehicle", flow = "Out", sortOrder = 10 })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync(categories,
            new { code = "RORO_DRIVEAWAY", nameEn = "Drive-away", nameAr = "خروج بالقيادة", mode = "Vehicle", flow = "Out", driveAway = true, sortOrder = 11 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync(categories,
            new { code = "BAD_DRIVE", nameEn = "x", nameAr = "y", mode = "Container", driveAway = true })).StatusCode);

        var company = await As(DemoDataSeeder.DispatcherEmail);
        var carrier = await company.PostAsJsonAsync(Appointments, new
        {
            slotId = SlotId, truckId = Demo.TruckIds[0],
            items = Vins.Select(v => new { category = "RORO_PICKUP", vin = v.ToLowerInvariant(), vehicleDesc = "Toyota Corolla", cargoRef = "NYKS260415AB" }).ToArray(),
        });
        Assert.Equal(HttpStatusCode.Created, carrier.StatusCode);
        var booked = JsonDocument.Parse(await carrier.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(Vins, booked.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("vin").GetString()).ToArray());
        Assert.Equal("Toyota Corolla", booked.GetProperty("items")[0].GetProperty("vehicleDesc").GetString());

        // One live visit per vehicle.
        var again = await company.PostAsJsonAsync(Appointments, new { slotId = SlotId, items = new[] { new { category = "RORO_PICKUP", vin = Vins[1] } } });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Contains(Vins[1], await again.Content.ReadAsStringAsync());

        // Customs holds work by VIN, as for containers.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await company.PostAsJsonAsync(Appointments,
            new { slotId = SlotId, items = new[] { new { category = "RORO_PICKUP", vin = "JN1TBNT30Z0123460" } } })).StatusCode);

        // Drive-away: the driver drives the vehicle out; no truck, now or later.
        Assert.Equal(HttpStatusCode.BadRequest, (await company.PostAsJsonAsync(Appointments,
            new { slotId = SlotId, truckId = Demo.TruckIds[1], items = new[] { new { category = "RORO_DRIVEAWAY", vin = "WVWZZZAWZKU012345" } } })).StatusCode);
        var drive = await company.PostAsJsonAsync(Appointments,
            new { slotId = SlotId, items = new[] { new { category = "RORO_DRIVEAWAY", vin = "WVWZZZAWZKU012345" } } });
        Assert.Equal(HttpStatusCode.Created, drive.StatusCode);

        // Vehicles and containers don't share a visit.
        Assert.Equal(HttpStatusCode.BadRequest, (await company.PostAsJsonAsync(Appointments, new
        {
            slotId = SlotId,
            items = new object[] { new { category = "RORO_PICKUP", vin = "WDD2050421R123456" }, new { category = "IMP_PICKUP", containerNo = "MSCU1234566" } },
        })).StatusCode);
    }
}
