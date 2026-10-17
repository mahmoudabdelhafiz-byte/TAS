using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yardsyncs.Application.Booking;
using Yardsyncs.Application.Gate;
using Yardsyncs.Domain.Booking;
using Yardsyncs.Domain.Gate;
using Yardsyncs.Infrastructure;
using Yardsyncs.Infrastructure.Persistence;

namespace Yardsyncs.Api.Tests;

/// <summary>The trouble desk books a walk-in; appointments are found on any day.</summary>
public sealed class WalkInSearchApiTests(YardsyncsApiFactory factory) : ApiTestBase(factory)
{
    private string Gate => $"{Terminal}/gate";

    [Fact]
    public async Task A_truck_without_an_appointment_is_booked_as_a_walk_in_and_let_in()
    {
        if (!Enabled) return;
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<YardsyncsDbContext>();
            var now = DateTimeOffset.UtcNow;
            await db.Slots.Where(s => s.SlotId == SlotId).ExecuteUpdateAsync(s => s.SetProperty(x => x.StartAt, now.AddMinutes(-10)).SetProperty(x => x.EndAt, now.AddMinutes(50)));
        }
        var clerk = await As(DemoDataSeeder.PlannerEmail);
        var arrival = (await Read<AdminResponse<ArrivalResult>>(await clerk.PostAsJsonAsync($"{Gate}/arrivals", new { laneCode = "IN-1", plateRead = "1234 ABJ" }))).Data;
        Assert.Equal((GateDecisionKind.Refer, TroubleReason.NoAppointment), (arrival.Decision, arrival.Reason));

        // A note is needed; then booked for the company in the slot running now, and let in.
        var noNote = await clerk.PostAsJsonAsync($"{Gate}/troubles/{arrival.TroubleId}/resolve",
            new { action = "WalkIn", walkIn = new { companyId = Demo.CompanyId, items = new[] { new { category = "IMP_PICKUP", containerNo = "MSCU1234566" } } } });
        Assert.Equal(HttpStatusCode.BadRequest, noNote.StatusCode);
        var resolved = await clerk.PostAsJsonAsync($"{Gate}/troubles/{arrival.TroubleId}/resolve", new
        {
            action = "WalkIn", note = "Driver had no booking; yard has room",
            walkIn = new { companyId = Demo.CompanyId, items = new[] { new { category = "IMP_PICKUP", containerNo = "MSCU1234566" } } },
        });
        Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);
        var trouble = (await Read<AdminResponse<TroubleView>>(resolved)).Data;
        Assert.Equal(TroubleResolution.WalkIn, trouble.Resolution);
        Assert.NotNull(trouble.AppointmentNo);

        var appointment = await Read<AppointmentDto>(await clerk.GetAsync($"{Appointments}/{trouble.AppointmentNo}"));
        Assert.Equal(AppointmentStatus.Arrived, appointment.Status);
        Assert.Equal("1234 ABJ", appointment.TruckPlate); // the plate read, matched to the company's truck
    }

    [Fact]
    public async Task Appointments_are_found_on_any_day_by_number_container_plate_or_reference()
    {
        if (!Enabled) return;
        var dispatcher = await As(DemoDataSeeder.DispatcherEmail);
        var booked = await Read<AppointmentDto>(await dispatcher.PostAsJsonAsync(Appointments, new
        {
            slotId = SlotId, truckId = Demo.TruckIds[0],
            items = new[] { new { category = "IMP_PICKUP", containerNo = "MSCU1234566" } },
        }));

        var planner = await As(DemoDataSeeder.PlannerEmail);
        foreach (var q in new[] { booked.AppointmentNo, "mscu1234566", "1234 abj" })
        {
            var found = await Read<List<AppointmentDto>>(await planner.GetAsync($"{Appointments}/search?q={Uri.EscapeDataString(q)}"));
            Assert.Contains(found, a => a.AppointmentNo == booked.AppointmentNo);
        }
        Assert.Empty(await Read<List<AppointmentDto>>(await planner.GetAsync($"{Appointments}/search?q=TGHU8812348")));
        Assert.Equal(HttpStatusCode.BadRequest, (await planner.GetAsync($"{Appointments}/search?q=ms")).StatusCode);

        // Another company finds nothing of Al Noor's.
        var other = await As(DemoDataSeeder.OtherDispatcherEmail);
        Assert.Empty(await Read<List<AppointmentDto>>(await other.GetAsync($"{Appointments}/search?q=MSCU1234566")));
    }
}
