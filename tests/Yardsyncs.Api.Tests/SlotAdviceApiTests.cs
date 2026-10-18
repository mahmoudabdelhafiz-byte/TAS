using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Yardsyncs.Infrastructure;

namespace Yardsyncs.Api.Tests;

/// <summary>The planner's suggestions from the history: who sees them, the setting, and deciding on one.</summary>
public sealed class SlotAdviceApiTests(YardsyncsApiFactory factory) : ApiTestBase(factory)
{
    private string Advice => $"{Terminal}/capacity/advice";

    [Fact]
    public async Task Planners_see_the_suggestions_and_the_setting_turns_them_off()
    {
        if (!Enabled) return;

        var planner = await As(DemoDataSeeder.PlannerEmail);
        var response = await planner.GetAsync(Advice);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var advice = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(1, advice.GetProperty("mode").GetInt32());
        // A new terminal has no four weeks of history yet: no suggestions, and the screen says why.
        Assert.False(advice.GetProperty("enoughHistory").GetBoolean());
        Assert.Equal(0, advice.GetProperty("suggestions").GetArrayLength());

        var settings = JsonDocument.Parse(await (await planner.GetAsync($"{Terminal}/settings")).Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(1, settings.GetProperty("slot_advice").GetInt32());
        Assert.Equal(20, settings.GetProperty("slot_target_wait").GetInt32());
        Assert.Equal(15, settings.GetProperty("slot_max_overbook").GetInt32());
        Assert.Equal(8, settings.GetProperty("slot_history_weeks").GetInt32());

        Assert.Equal(HttpStatusCode.BadRequest, (await planner.PutAsJsonAsync($"{Terminal}/settings",
            new { values = new Dictionary<string, int> { ["slot_history_weeks"] = 2 }, reason = (string?)null })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await planner.PutAsJsonAsync($"{Terminal}/settings",
            new { values = new Dictionary<string, int> { ["slot_advice"] = 0 }, reason = (string?)null })).StatusCode);
        var off = JsonDocument.Parse(await (await planner.GetAsync(Advice)).Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(0, off.GetProperty("mode").GetInt32());
        Assert.Equal(HttpStatusCode.OK, (await planner.PutAsJsonAsync($"{Terminal}/settings",
            new { values = new Dictionary<string, int> { ["slot_advice"] = 1 }, reason = (string?)null })).StatusCode);
    }

    [Fact]
    public async Task Only_terminal_staff_see_them_and_an_old_suggestion_cannot_be_decided()
    {
        if (!Enabled) return;

        var company = await As(DemoDataSeeder.DispatcherEmail);
        Assert.Equal(HttpStatusCode.Forbidden, (await company.GetAsync(Advice)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await company.PostAsJsonAsync($"{Advice}/decide", new { key = "overbook|Sunday|06:00", approve = true })).StatusCode);

        var planner = await As(DemoDataSeeder.PlannerEmail);
        var gone = await planner.PostAsJsonAsync($"{Advice}/decide", new { key = "overbook|Sunday|06:00", approve = true });
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        Assert.Contains("no longer current", await gone.Content.ReadAsStringAsync());
    }
}
