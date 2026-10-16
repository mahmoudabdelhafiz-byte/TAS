using System.Net;
using System.Net.Http.Json;
using Yardsyncs.Application.Booking;
using Yardsyncs.Infrastructure;

namespace Yardsyncs.Api.Tests;

/// <summary>On-demand re-check and start-trip through the API (the logic itself is covered by the database tests).</summary>
public sealed class RevalidationApiTests(YardsyncsApiFactory factory) : ApiTestBase(factory)
{
    [Fact]
    public async Task Owners_and_staff_can_recheck_others_cannot()
    {
        if (!Enabled) return;

        var alNoor = await As(DemoDataSeeder.DispatcherEmail);
        var gulf = await As(DemoDataSeeder.OtherDispatcherEmail);
        var planner = await As(DemoDataSeeder.PlannerEmail);

        var booked = await Read<AppointmentDto>(await alNoor.PostAsJsonAsync(Appointments, ImportBooking()));
        var recheck = $"{Appointments}/{booked.AppointmentNo}/revalidate";

        var mine = await alNoor.PostAsync(recheck, null);
        Assert.Equal(HttpStatusCode.OK, mine.StatusCode);
        Assert.Equal(booked.AppointmentNo, (await Read<AppointmentDto>(mine)).AppointmentNo);
        Assert.Equal(HttpStatusCode.OK, (await planner.PostAsync(recheck, null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await gulf.PostAsync(recheck, null)).StatusCode);

        // Starting the trip for tomorrow's slot is either too early (409) or allowed (200), depending on the time now.
        var start = await alNoor.PostAsync($"{Appointments}/{booked.AppointmentNo}/start-trip", null);
        Assert.Contains(start.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Conflict });
        Assert.Equal(HttpStatusCode.NotFound, (await gulf.PostAsync($"{Appointments}/{booked.AppointmentNo}/start-trip", null)).StatusCode);
    }
}
