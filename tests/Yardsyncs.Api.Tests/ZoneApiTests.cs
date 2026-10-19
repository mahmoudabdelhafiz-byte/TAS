using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yardsyncs.Infrastructure;
using Yardsyncs.Infrastructure.Persistence;

namespace Yardsyncs.Api.Tests;

/// <summary>A terminal in a zone run by Nafith (its simulator): customs clearance at booking, events from the yard.</summary>
public sealed class ZoneFactory : YardsyncsApiFactory
{
    protected override void Extra(IWebHostBuilder builder)
    {
        builder.UseSetting("Nafith:Mode", "Simulator");
        builder.UseSetting("Nafith:InboundKey", "test-zone-key");
        builder.UseSetting("Nafith:SimulatorNotCleared:0", "CAIU5522102");
        builder.UseSetting("Integration:Terminals:DEMO:Url", "https://zone.example/yardsyncs");
        builder.UseSetting("Integration:Terminals:DEMO:Events:0", "APPOINTMENT_BOOKED");
        builder.UseSetting("Integration:Terminals:DEMO:Events:1", "APPOINTMENT_CANCELLED");
    }
}

public sealed class ZoneApiTests(ZoneFactory factory) : ApiTestBase(factory), IClassFixture<ZoneFactory>
{
    private async Task<HttpResponseMessage> BookAsync(string container) =>
        await (await As(DemoDataSeeder.DispatcherEmail)).PostAsJsonAsync(Appointments, new
        {
            slotId = SlotId,
            truckId = Demo.TruckIds[0],
            driverId = Demo.DriverIds[0],
            items = new[] { new { category = "IMP_PICKUP", containerNo = container } },
        });

    private HttpRequestMessage ZoneEvent(object body, string? key = "test-zone-key")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/zone/events") { Content = JsonContent.Create(body) };
        if (key is not null)
            request.Headers.Add("X-Zone-Key", key);
        return request;
    }

    [Fact]
    public async Task A_container_not_cleared_in_the_zone_is_refused_and_a_cleared_one_is_announced()
    {
        if (!Enabled) return;

        var refused = await BookAsync("CAIU5522102");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Contains("not cleared by customs in NAFITH", await refused.Content.ReadAsStringAsync());

        var booked = await BookAsync("MSCU1234566");
        Assert.Equal(HttpStatusCode.Created, booked.StatusCode);
        var number = (await Read<Yardsyncs.Application.Booking.AppointmentDto>(booked)).AppointmentNo;

        // The pre-announcement for the zone's webhook, with the truck and the move.
        var planner = await As(DemoDataSeeder.PlannerEmail);
        var events = await Read<List<Yardsyncs.Infrastructure.Integration.IntegrationEventView>>(await planner.GetAsync($"{Terminal}/integration-events"));
        var announced = Assert.Single(events, e => e.EventType == "APPOINTMENT_BOOKED" && e.AppointmentNo == number);
        Assert.Equal("MSCU1234566", announced.Payload.GetProperty("moves")[0].GetProperty("container").GetString());
        Assert.Equal("DEMO", announced.Payload.GetProperty("terminal").GetString());
    }

    [Fact]
    public async Task The_zone_tells_when_a_truck_enters_its_yard_and_needs_its_key()
    {
        if (!Enabled) return;

        var booked = await BookAsync("MSCU1234566");
        Assert.Equal(HttpStatusCode.Created, booked.StatusCode);
        var number = (await Read<Yardsyncs.Application.Booking.AppointmentDto>(booked)).AppointmentNo;
        var client = Factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(ZoneEvent(new { type = "YARD_ENTERED", terminal = "DEMO", appointmentNo = number }, null))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(ZoneEvent(new { type = "YARD_ENTERED", terminal = "DEMO", appointmentNo = number }, "wrong"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(ZoneEvent(new { type = "SOMETHING", terminal = "DEMO", appointmentNo = number }))).StatusCode);

        var entered = await client.SendAsync(ZoneEvent(new { type = "YARD_ENTERED", terminal = "DEMO", appointmentNo = number, message = "Bay 14" }));
        Assert.Equal(HttpStatusCode.OK, entered.StatusCode);
        Assert.Contains(number, await entered.Content.ReadAsStringAsync());

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<YardsyncsDbContext>();
        var visit = await db.Appointments.AsNoTracking().Include(a => a.History).SingleAsync(a => a.AppointmentNo == number);
        Assert.Equal(Yardsyncs.Domain.Booking.AppointmentStatus.EnRoute, visit.Status);
        Assert.Contains(visit.History, h => h.Reason == "In the zone's truck yard: Bay 14");

        // A clearance change re-checks the visits with that container at once.
        var changed = await client.SendAsync(ZoneEvent(new { type = "CLEARANCE_CHANGED", terminal = "DEMO", containerNo = "MSCU 123456-6" }));
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.Contains(number, await changed.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(ZoneEvent(new { type = "YARD_RELEASED", terminal = "DEMO", plate = "0000 XXX" }))).StatusCode);
    }
}
