using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yardsyncs.Application.Gate;
using Yardsyncs.Application.MasterData;
using Yardsyncs.Domain;
using Yardsyncs.Domain.Gate;
using Yardsyncs.Domain.Parties;
using Yardsyncs.Infrastructure;
using Yardsyncs.Infrastructure.DriverApp;
using Yardsyncs.Infrastructure.Persistence;

namespace Yardsyncs.Api.Tests;

/// <summary>The driver record: entries with points, who sees them, disputes, and the warning at the gate.</summary>
public sealed class DriverRecordApiTests(YardsyncsApiFactory factory) : ApiTestBase(factory)
{
    private const string Abdullah = "0500000001"; // demo driver, +966500000001, Al Noor

    private string Records => $"{Terminal}/driver-records";

    private async Task<HttpClient> DriverAppAsync()
    {
        var start = await Factory.CreateClient().PostAsJsonAsync("/api/driver/login/start", new { mobile = Abdullah, installId = "install-record-01" });
        var code = JsonDocument.Parse(await start.Content.ReadAsStringAsync()).RootElement.GetProperty("testCode").GetString();
        var verify = await Factory.CreateClient().PostAsJsonAsync("/api/driver/login/verify",
            new { mobile = Abdullah, installId = "install-record-01", code, platform = "android", appVersion = "0.1.0" });
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await Read<DriverSession>(verify)).Token);
        return client;
    }

    private async Task<string> BookAsync()
    {
        var alNoor = await As(DemoDataSeeder.DispatcherEmail);
        var booked = await alNoor.PostAsJsonAsync(Appointments, new
        {
            slotId = SlotId,
            truckId = Demo.TruckIds[0],
            driverId = Demo.DriverIds[0],
            items = new[] { new { category = "IMP_PICKUP", containerNo = "MSCU1234566" } },
        });
        Assert.Equal(HttpStatusCode.Created, booked.StatusCode);
        return (await Read<Yardsyncs.Application.Booking.AppointmentDto>(booked)).AppointmentNo;
    }

    [Fact]
    public async Task Staff_record_the_driver_and_the_company_sees_and_the_driver_disputes()
    {
        if (!Enabled) return;

        var number = await BookAsync();
        var clerk = await As(DemoDataSeeder.TerminalAdminEmail);
        var added = await clerk.PostAsJsonAsync($"{Records}/{Demo.DriverIds[0]}/incidents",
            new { kind = "Speeding", appointmentNo = number, note = "45 km/h on the quay road" });
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        var record = (await Read<AdminResponse<DriverRecordView>>(added)).Data;
        Assert.Equal((3, DriverStanding.Clear), (record.Points, record.Standing));
        var entry = Assert.Single(record.Incidents);
        Assert.Equal((IncidentKind.Speeding, number, true), (entry.Kind, entry.AppointmentNo, entry.Counts));
        Assert.NotNull(entry.RecordedByName);

        // The driver is told on the phone, in their language.
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<YardsyncsDbContext>();
            var queued = await db.NotificationLogs.SingleAsync(n => n.TemplateCode == NotificationTemplates.DriverIncident);
            Assert.Equal((Demo.DriverIds[0], "ar"), (queued.DriverId, queued.Language));
        }

        // Al Noor sees its driver (without the staff names); Gulf Haulage does not.
        var alNoor = await As(DemoDataSeeder.DispatcherEmail);
        var seen = await Read<DriverRecordView>(await alNoor.GetAsync($"{Records}/{Demo.DriverIds[0]}"));
        Assert.Equal(3, seen.Points);
        Assert.Null(Assert.Single(seen.Incidents).RecordedByName);
        Assert.Equal("Abdullah Al-Harbi", Assert.Single(await Read<List<DriverPointsRow>>(await alNoor.GetAsync(Records))).FullName);
        var gulf = await As(DemoDataSeeder.OtherDispatcherEmail);
        Assert.Equal(HttpStatusCode.NotFound, (await gulf.GetAsync($"{Records}/{Demo.DriverIds[0]}")).StatusCode);
        Assert.Empty(await Read<List<DriverPointsRow>>(await gulf.GetAsync(Records)));
        Assert.Equal(HttpStatusCode.Forbidden, (await alNoor.PostAsJsonAsync($"{Records}/{Demo.DriverIds[0]}/incidents", new { kind = "NoPpe" })).StatusCode);

        // The driver sees it in the app and disputes it once; the planner accepts: the points are gone.
        var app = await DriverAppAsync();
        var mine = await Read<MyRecordView>(await app.GetAsync("/api/driver/record"));
        var item = Assert.Single(mine.Incidents);
        Assert.True(item.CanDispute);
        Assert.Equal(3, Assert.Single(mine.Terminals).Points);
        Assert.Equal(HttpStatusCode.OK, (await app.PostAsJsonAsync($"/api/driver/incidents/{item.IncidentId}/dispute", new { text = "I was at 25 km/h, the camera is wrong" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await app.PostAsJsonAsync($"/api/driver/incidents/{item.IncidentId}/dispute", new { text = "again" })).StatusCode);

        Assert.Equal(1, Assert.Single(await Read<List<DriverPointsRow>>(await clerk.GetAsync(Records))).Open);
        var gateClerk = await As(DemoDataSeeder.PlannerEmail);
        var decided = await gateClerk.PostAsJsonAsync($"{Terminal}/driver-incidents/{item.IncidentId}/decision", new { accept = true, answer = "The speed camera was checked: it was wrong." });
        Assert.Equal(HttpStatusCode.OK, decided.StatusCode);
        Assert.Equal(0, (await Read<AdminResponse<DriverRecordView>>(decided)).Data.Points);
        var after = Assert.Single((await Read<MyRecordView>(await app.GetAsync("/api/driver/record"))).Incidents);
        Assert.Equal((DisputeStatus.Accepted, false), (after.DisputeStatus, after.Counts));

        // The terminal can stop sharing records with companies.
        Assert.Equal(HttpStatusCode.OK, (await clerk.PutAsJsonAsync($"{Terminal}/settings",
            new { values = new Dictionary<string, int> { ["driver_record_company"] = 0 }, reason = (string?)null })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await alNoor.GetAsync(Records)).StatusCode);
    }

    [Fact]
    public async Task The_gate_warns_about_a_driver_with_points_and_entries_can_be_withdrawn()
    {
        if (!Enabled) return;

        var number = await BookAsync();
        var admin = await As(DemoDataSeeder.TerminalAdminEmail);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"{Terminal}/settings",
            new { values = new Dictionary<string, int> { ["driver_warn_points"] = 2 }, reason = (string?)null })).StatusCode);
        var added = (await Read<AdminResponse<DriverRecordView>>(
            await admin.PostAsJsonAsync($"{Records}/{Demo.DriverIds[0]}/incidents", new { kind = "NoPpe" }))).Data;
        Assert.Equal(DriverStanding.Warning, added.Standing);

        var check = await admin.PostAsJsonAsync($"{Terminal}/gate/arrivals/check", new { laneCode = "IN-1", appointmentNo = number });
        Assert.Equal(HttpStatusCode.OK, check.StatusCode);
        var arrival = (await Read<AdminResponse<ArrivalResult>>(check)).Data;
        Assert.Contains(arrival.Warnings, w => w == "Driver Abdullah Al-Harbi has 2 penalty points at this terminal.");

        var incident = added.Incidents.Single();
        var planner = await As(DemoDataSeeder.PlannerEmail);
        Assert.Equal(HttpStatusCode.BadRequest, (await planner.PostAsJsonAsync($"{Terminal}/driver-incidents/{incident.IncidentId}/withdraw", new { reason = "" })).StatusCode);
        var withdrawn = await planner.PostAsJsonAsync($"{Terminal}/driver-incidents/{incident.IncidentId}/withdraw", new { reason = "Wrong driver" });
        Assert.Equal(HttpStatusCode.OK, withdrawn.StatusCode);
        Assert.Equal(0, (await Read<AdminResponse<DriverRecordView>>(withdrawn)).Data.Points);
        Assert.DoesNotContain((await Read<AdminResponse<ArrivalResult>>(await admin.PostAsJsonAsync($"{Terminal}/gate/arrivals/check",
            new { laneCode = "IN-1", appointmentNo = number }))).Data.Warnings, w => w.Contains("penalty points"));

        var dispatcher = await As(DemoDataSeeder.DispatcherEmail);
        Assert.Equal(HttpStatusCode.Forbidden, (await dispatcher.PostAsJsonAsync($"{Terminal}/driver-incidents/{incident.IncidentId}/withdraw", new { reason = "x" })).StatusCode);
    }
}
