using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Yardsyncs.Application.Gate;
using Yardsyncs.Application.Safety;
using Yardsyncs.Infrastructure;

namespace Yardsyncs.Api.Tests;

/// <summary>Safety in the driver app: a campaign confirmed in the app, the induction passed, and what the gate says.</summary>
public sealed class SafetyApiTests(YardsyncsApiFactory factory) : ApiTestBase(factory)
{
    private const string Abdullah = "0500000001"; // demo driver, +966500000001

    private string Safety => $"{Terminal}/safety";

    private async Task<HttpClient> DriverAppAsync(string language)
    {
        var start = await Factory.CreateClient().PostAsJsonAsync("/api/driver/login/start", new { mobile = Abdullah, installId = "install-safety-1" });
        var code = JsonDocument.Parse(await start.Content.ReadAsStringAsync()).RootElement.GetProperty("testCode").GetString();
        var verify = await Factory.CreateClient().PostAsJsonAsync("/api/driver/login/verify",
            new { mobile = Abdullah, installId = "install-safety-1", code, platform = "android", appVersion = "0.1.0" });
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await Read<Yardsyncs.Infrastructure.DriverApp.DriverSession>(verify)).Token);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(language);
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

    private async Task<IReadOnlyList<string>> GateWarningsAsync(HttpClient staff, string number) =>
        (await Read<AdminResponse<ArrivalResult>>(await staff.PostAsJsonAsync($"{Terminal}/gate/arrivals/check", new { laneCode = "IN-1", appointmentNo = number }))).Data.Warnings;

    [Fact]
    public async Task A_campaign_is_read_in_the_drivers_language_and_confirmed()
    {
        if (!Enabled) return;

        var number = await BookAsync();
        var planner = await As(DemoDataSeeder.PlannerEmail);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var created = await planner.PostAsJsonAsync($"{Safety}/campaigns", new
        {
            texts = new Dictionary<string, object>
            {
                ["en"] = new { title = "Wear your vest", body = "Outside the cab, always wear the high-visibility vest." },
                ["ar"] = new { title = "ارتدِ السترة", body = "خارج المقصورة ارتدِ السترة العاكسة دائماً." },
            },
            imageUrl = (string?)null,
            startsOn = today.AddDays(-1),
            endsOn = today.AddDays(30),
            requireAck = true,
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var campaign = (await Read<AdminResponse<CampaignView>>(created)).Data;
        Assert.True(campaign.Active);

        var dispatcher = await As(DemoDataSeeder.DispatcherEmail);
        Assert.Equal(HttpStatusCode.Forbidden, (await dispatcher.GetAsync($"{Safety}/campaigns")).StatusCode);
        Assert.Contains(await GateWarningsAsync(planner, number), w => w.Contains("has not confirmed the safety notice \"Wear your vest\""));

        var app = await DriverAppAsync("ar");
        var mine = JsonDocument.Parse(await (await app.GetAsync("/api/driver/safety")).Content.ReadAsStringAsync()).RootElement;
        var notice = mine.GetProperty("campaigns")[0];
        Assert.Equal("ارتدِ السترة", notice.GetProperty("title").GetString());
        Assert.False(notice.GetProperty("confirmed").GetBoolean());

        Assert.Equal(HttpStatusCode.OK, (await app.PostAsync($"/api/driver/safety/campaigns/{campaign.CampaignId}/confirm", null)).StatusCode);
        Assert.Equal(1, Assert.Single(await Read<List<CampaignView>>(await planner.GetAsync($"{Safety}/campaigns"))).Confirmed);
        Assert.DoesNotContain(await GateWarningsAsync(planner, number), w => w.Contains("safety notice"));

        var archived = await planner.PostAsync($"{Safety}/campaigns/{campaign.CampaignId}/archive", null);
        Assert.False((await Read<AdminResponse<CampaignView>>(archived)).Data.Active);
    }

    [Fact]
    public async Task The_induction_is_passed_with_every_answer_right_and_the_gate_checks_it()
    {
        if (!Enabled) return;

        var number = await BookAsync();
        var admin = await As(DemoDataSeeder.TerminalAdminEmail);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.GetAsync($"{Safety}/induction")).StatusCode);
        var saved = await admin.PutAsJsonAsync($"{Safety}/induction", new
        {
            pages = new[] { new { texts = new Dictionary<string, object> { ["en"] = new { title = "Speed", body = "20 km/h everywhere in the yard." } } } },
            questions = new[]
            {
                new { texts = new Dictionary<string, object> { ["en"] = new { question = "The speed limit in the yard?", options = new[] { "40 km/h", "20 km/h" } } }, correct = 1 },
                new { texts = new Dictionary<string, object> { ["en"] = new { question = "Where do you wait while the crane works?", options = new[] { "In the cab", "Next to the truck" } } }, correct = 0 },
            },
            validMonths = 12,
        });
        Assert.Equal(HttpStatusCode.Created, saved.StatusCode);
        Assert.Equal(1, (await Read<AdminResponse<InductionView>>(saved)).Data.Version);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"{Terminal}/settings",
            new { values = new Dictionary<string, int> { ["induction_mode"] = 2 }, reason = (string?)null })).StatusCode);

        Assert.Contains(await GateWarningsAsync(admin, number), w => w == "Driver Abdullah Al-Harbi has no valid safety induction for this terminal.");

        var app = await DriverAppAsync("en");
        var inductions = JsonDocument.Parse(await (await app.GetAsync("/api/driver/safety")).Content.ReadAsStringAsync()).RootElement.GetProperty("inductions");
        Assert.True(inductions[0].GetProperty("needed").GetBoolean());
        Assert.Equal("The speed limit in the yard?", inductions[0].GetProperty("questions")[0].GetProperty("question").GetString());

        var wrong = (await Read<AdminResponse<AnswerResult>>(await app.PostAsJsonAsync($"/api/driver/safety/inductions/{Demo.TerminalId}/answers", new { answers = new[] { 1, 1 } }))).Data;
        Assert.Equal((false, 1), (wrong.Passed, Assert.Single(wrong.Wrong)));
        var right = (await Read<AdminResponse<AnswerResult>>(await app.PostAsJsonAsync($"/api/driver/safety/inductions/{Demo.TerminalId}/answers", new { answers = new[] { 1, 0 } }))).Data;
        Assert.True(right.Passed);
        Assert.NotNull(right.ValidUntil);

        Assert.DoesNotContain(await GateWarningsAsync(admin, number), w => w.Contains("safety induction"));
        Assert.Equal(1, (await Read<InductionView>(await admin.GetAsync($"{Safety}/induction"))).ValidPasses);
    }
}
