using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Yardsyncs.Infrastructure;

namespace Yardsyncs.Api.Tests;

/// <summary>How quickly each hour gets through the gate, shown when booking: no figure without history; the setting.</summary>
public sealed class GateSpeedApiTests(YardsyncsApiFactory factory) : ApiTestBase(factory)
{
    [Fact]
    public async Task Booking_hours_carry_no_speed_without_history_and_the_setting_can_be_switched()
    {
        if (!Enabled) return;

        var company = await As(DemoDataSeeder.DispatcherEmail);
        var response = await company.GetAsync($"{Terminal}/availability?date={Tomorrow:yyyy-MM-dd}&category=IMP_PICKUP");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var slots = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.True(slots.GetArrayLength() > 0);
        foreach (var slot in slots.EnumerateArray())
        {
            // A new terminal has no gate history: no guess.
            Assert.Equal(JsonValueKind.Null, slot.GetProperty("gateSpeed").ValueKind);
            Assert.Equal(JsonValueKind.Null, slot.GetProperty("expectedWaitMinutes").ValueKind);
        }

        var planner = await As(DemoDataSeeder.PlannerEmail);
        var settings = JsonDocument.Parse(await (await planner.GetAsync($"{Terminal}/settings")).Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(1, settings.GetProperty("slot_gate_speed").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, (await planner.PutAsJsonAsync($"{Terminal}/settings",
            new { values = new Dictionary<string, int> { ["slot_gate_speed"] = 2 }, reason = (string?)null })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await planner.PutAsJsonAsync($"{Terminal}/settings",
            new { values = new Dictionary<string, int> { ["slot_gate_speed"] = 0 }, reason = (string?)null })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await company.GetAsync($"{Terminal}/availability?date={Tomorrow:yyyy-MM-dd}&category=IMP_PICKUP")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await planner.PutAsJsonAsync($"{Terminal}/settings",
            new { values = new Dictionary<string, int> { ["slot_gate_speed"] = 1 }, reason = (string?)null })).StatusCode);
    }
}
