using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Tas.Application.Capacity;
using Tas.Infrastructure;
using Tas.Infrastructure.Capacity;

namespace Tas.Api.Tests;

/// <summary>The planner's configuration flows, end to end through the API.</summary>
public sealed class CapacityAdminApiTests(TasApiFactory factory) : ApiTestBase(factory)
{
    private string Capacity => $"{Terminal}/capacity";

    private async Task<SlotBoardRow> BoardRowAsync(HttpClient client, DateOnly date, int hour)
    {
        var board = await Read<List<SlotBoardRow>>(await client.GetAsync($"{Capacity}/slot-board?date={date:yyyy-MM-dd}"));
        return board.Single(r => r.StartAt == At(date, hour));
    }

    private async Task<int> AvailableAsync(HttpClient client, string category, int hour = 10)
    {
        var slots = await Read<List<SlotAvailability>>(
            await client.GetAsync($"{Terminal}/availability?date={Tomorrow:yyyy-MM-dd}&category={category}"));
        return slots.Single(s => s.StartAt == At(Tomorrow, hour)).Available;
    }

    private object CloseOverride(string reason = "Crane 3 maintenance") => new
    {
        startAt = At(Tomorrow, 10),
        endAt = At(Tomorrow, 11),
        action = "Close",
        reason,
    };

    [Fact]
    public async Task Companies_cannot_see_or_change_capacity()
    {
        if (!Enabled) return;

        var dispatcher = await As(DemoDataSeeder.DispatcherEmail);

        Assert.Equal(HttpStatusCode.Forbidden, (await dispatcher.GetAsync($"{Capacity}/templates")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await dispatcher.GetAsync($"{Capacity}/slot-board?date={Tomorrow:yyyy-MM-dd}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await dispatcher.PostAsJsonAsync($"{Capacity}/overrides", CloseOverride())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await dispatcher.GetAsync($"{Terminal}/audit")).StatusCode);
    }

    [Fact]
    public async Task Closing_a_slot_takes_effect_immediately_and_is_audited()
    {
        if (!Enabled) return;

        var planner = await As(DemoDataSeeder.PlannerEmail);
        var dispatcher = await As(DemoDataSeeder.DispatcherEmail);
        Assert.True(await AvailableAsync(dispatcher, "IMP_PICKUP") > 0);

        var created = await planner.PostAsJsonAsync($"{Capacity}/overrides", CloseOverride());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var response = await Read<AdminResponse<OverrideView>>(created);
        Assert.NotNull(response.RegeneratedSlots);
        Assert.Equal("Crane 3 maintenance", response.Data.Reason);

        // Slot board and availability reflect it at once; booking is refused.
        Assert.True((await BoardRowAsync(planner, Tomorrow, 10)).IsClosed);
        Assert.False((await BoardRowAsync(planner, Tomorrow, 11)).IsClosed);
        Assert.Equal(0, await AvailableAsync(dispatcher, "IMP_PICKUP"));
        Assert.Equal(HttpStatusCode.Conflict, (await dispatcher.PostAsJsonAsync(Appointments, ImportBooking())).StatusCode);

        // Lifting the override reopens the slot.
        var lifted = await planner.PostAsJsonAsync($"{Capacity}/overrides/{response.Data.OverrideId}/deactivate", new { reason = "Crane repaired early" });
        Assert.Equal(HttpStatusCode.OK, lifted.StatusCode);
        Assert.False((await BoardRowAsync(planner, Tomorrow, 10)).IsClosed);
        Assert.Equal(HttpStatusCode.Created, (await dispatcher.PostAsJsonAsync(Appointments, ImportBooking())).StatusCode);

        var audit = await Read<List<AuditEntryView>>(await planner.GetAsync($"{Terminal}/audit?entity=capacity_override"));
        Assert.Equal(new[] { "DELETE", "INSERT" }, audit.Select(a => a.Action).ToArray());
        Assert.Equal("Crane repaired early", audit[0].Reason);
        Assert.Equal(Demo.PlannerUserId, audit[1].ChangedBy);
        Assert.Contains("Crane 3 maintenance", audit[1].NewValues);
    }

    [Fact]
    public async Task Cutting_capacity_below_bookings_keeps_them_and_flags_the_slot()
    {
        if (!Enabled) return;

        var planner = await As(DemoDataSeeder.PlannerEmail);
        var dispatcher = await As(DemoDataSeeder.DispatcherEmail);
        Assert.Equal(HttpStatusCode.Created, (await dispatcher.PostAsJsonAsync(Appointments, ImportBooking(container: "MSCU1234566"))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await dispatcher.PostAsJsonAsync(Appointments, ImportBooking(container: "MAEU4001110"))).StatusCode);

        var setOne = await planner.PostAsJsonAsync($"{Capacity}/overrides", new
        {
            startAt = At(Tomorrow, 10), endAt = At(Tomorrow, 11), action = "Set", value = 1, reason = "Only one lane open",
        });
        Assert.Equal(HttpStatusCode.Created, setOne.StatusCode);

        var row = await BoardRowAsync(planner, Tomorrow, 10);
        Assert.Equal(1, row.TotalCapacity);
        Assert.Equal(2, row.TotalBooked);
        Assert.True(row.OverCapacity);
    }

    [Fact]
    public async Task Editing_the_weekly_template_regenerates_slots()
    {
        if (!Enabled) return;

        var planner = await As(DemoDataSeeder.PlannerEmail);
        var templates = await Read<List<TemplateView>>(await planner.GetAsync($"{Capacity}/templates"));
        var template = Assert.Single(templates);
        Assert.True(template.IsDefault);

        // Same hours, but 10 trucks per slot, no overbooking and no quotas.
        var edited = new TemplateInput(template.Name, true, null, null, 0,
            template.Windows.Select(w => new TemplateWindowInput(w.Day, w.Start, w.End, 10)).ToList(), "New gate layout");
        var saved = await planner.PutAsJsonAsync($"{Capacity}/templates/{template.TemplateId}", edited, Json);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        var row = await BoardRowAsync(planner, Tomorrow, 10);
        Assert.Equal(10, row.TotalCapacity);
        Assert.All(row.Categories, c => Assert.Equal(10, c.Quota));

        // Overlapping windows are refused with a clear message; the default can't be removed.
        var overlapping = new TemplateInput(template.Name, true, null, null, 0,
            [new TemplateWindowInput(DayOfWeek.Sunday, "06:00", "12:00", 10), new TemplateWindowInput(DayOfWeek.Sunday, "11:00", "14:00", 10)]);
        var refused = await planner.PutAsJsonAsync($"{Capacity}/templates/{template.TemplateId}", overlapping, Json);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("overlap", await refused.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.Conflict,
            (await planner.PostAsJsonAsync($"{Capacity}/templates/{template.TemplateId}/deactivate", new { reason = "test" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await planner.PostAsJsonAsync($"{Capacity}/templates", edited, Json)).StatusCode); // second default
    }

    [Fact]
    public async Task Seasonal_template_replaces_the_default_only_on_its_dates()
    {
        if (!Enabled) return;

        var planner = await As(DemoDataSeeder.PlannerEmail);
        var seasonal = new TemplateInput("Eid special hours", false, Tomorrow, Tomorrow, 1,
            Enum.GetValues<DayOfWeek>().Select(d => new TemplateWindowInput(d, "08:00", "12:00", 7)).ToList());

        var created = await planner.PostAsJsonAsync($"{Capacity}/templates", seasonal, Json);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var tomorrow = await Read<List<SlotBoardRow>>(await planner.GetAsync($"{Capacity}/slot-board?date={Tomorrow:yyyy-MM-dd}"));
        var open = tomorrow.Where(r => !r.IsClosed).ToList();
        Assert.Equal(4, open.Count);
        Assert.All(open, r => Assert.Equal(7, r.TotalCapacity));
        Assert.Equal(At(Tomorrow, 8), open[0].StartAt);

        var dayAfter = await Read<List<SlotBoardRow>>(await planner.GetAsync($"{Capacity}/slot-board?date={Tomorrow.AddDays(1):yyyy-MM-dd}"));
        Assert.True(dayAfter.Count(r => !r.IsClosed) > 4);
        Assert.DoesNotContain(dayAfter, r => r.TotalCapacity == 7);
    }

    [Fact]
    public async Task Reservations_hold_capacity_back_from_other_companies()
    {
        if (!Enabled) return;

        var planner = await As(DemoDataSeeder.PlannerEmail);
        var alNoor = await As(DemoDataSeeder.DispatcherEmail);
        var gulf = await As(DemoDataSeeder.OtherDispatcherEmail);
        var before = await AvailableAsync(gulf, "EXP_DROP");
        var row = await BoardRowAsync(planner, Tomorrow, 10);

        const int held = 25;
        var reservation = await planner.PostAsJsonAsync($"{Capacity}/reservations", new
        {
            companyId = Demo.CompanyId,
            startAt = At(Tomorrow, 0),
            endAt = At(Tomorrow.AddDays(1), 0),
            unitsPerSlot = held,
            releaseHoursBefore = 0,
            reason = "Al Noor contract",
        });
        Assert.Equal(HttpStatusCode.Created, reservation.StatusCode);

        // Held back from Gulf Haulage (on top of the demo's 2-truck walk-in reserve), not from Al Noor itself.
        const int walkIn = 2;
        var expected = Math.Max(0, Math.Min(before, row.TotalCapacity - row.TotalBooked - walkIn - held));
        Assert.Equal(expected, await AvailableAsync(gulf, "EXP_DROP"));
        Assert.True(expected < before);
        Assert.Equal(before, await AvailableAsync(alNoor, "EXP_DROP"));

        var listed = await Read<List<ReservationView>>(await planner.GetAsync($"{Capacity}/reservations"));
        Assert.Contains(listed, r => r.CompanyId == Demo.CompanyId && r.UnitsPerSlot == held);
    }

    [Fact]
    public async Task Settings_and_release_rules_are_validated_and_audited()
    {
        if (!Enabled) return;

        var admin = await As(DemoDataSeeder.TerminalAdminEmail);
        var gateClerkView = await As(DemoDataSeeder.PlannerEmail);

        var updated = await admin.PutAsJsonAsync($"{Terminal}/settings", new { values = new Dictionary<string, int> { ["early_grace_min"] = 45 }, reason = "Longer queue" });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var settings = await Read<Dictionary<string, int>>(await gateClerkView.GetAsync($"{Terminal}/settings"));
        Assert.Equal(45, settings["early_grace_min"]);
        Assert.Equal(30, settings["late_grace_min"]);

        Assert.Equal(HttpStatusCode.BadRequest,
            (await admin.PutAsJsonAsync($"{Terminal}/settings", new { values = new Dictionary<string, int> { ["coffee_breaks"] = 2 } })).StatusCode);

        var rules = await admin.PutAsJsonAsync($"{Capacity}/release-rules", new
        {
            rules = new[] { new { category = "MTY_RETURN", hoursBefore = 6 }, new { category = "EXP_DROP", hoursBefore = 4 } },
            reason = "Release unused quota earlier",
        });
        Assert.Equal(HttpStatusCode.OK, rules.StatusCode);
        var current = await Read<List<ReleaseRuleView>>(await admin.GetAsync($"{Capacity}/release-rules"));
        Assert.Equal(new[] { "EXP_DROP", "MTY_RETURN" }, current.Select(r => r.Category).ToArray());
        Assert.Equal(6, current.Single(r => r.Category == "MTY_RETURN").HoursBefore);

        var audit = await Read<List<AuditEntryView>>(await admin.GetAsync($"{Terminal}/audit"));
        Assert.Contains(audit, a => a.Entity == "terminal_setting" && a.Reason == "Longer queue");
        Assert.Contains(audit, a => a.Entity == "quota_release_rule" && a.OldValues!.Contains("12") && a.NewValues!.Contains("6"));
        using var json = JsonDocument.Parse(audit.First(a => a.Entity == "terminal_setting").NewValues!);
        Assert.Equal(45, json.RootElement.GetProperty("early_grace_min").GetInt32());
    }

    [Fact]
    public async Task Kpis_for_staff_only_with_a_bounded_range()
    {
        if (!Enabled) return;

        var alNoor = await As(DemoDataSeeder.DispatcherEmail);
        var planner = await As(DemoDataSeeder.PlannerEmail);
        Assert.Equal(HttpStatusCode.Created, (await alNoor.PostAsJsonAsync(Appointments, ImportBooking())).StatusCode);

        var kpis = $"{Terminal}/kpis?from={Tomorrow:yyyy-MM-dd}&to={Tomorrow:yyyy-MM-dd}";
        Assert.Equal(HttpStatusCode.Forbidden, (await alNoor.GetAsync(kpis)).StatusCode);

        var report = await Read<Tas.Application.Reports.KpiReport>(await planner.GetAsync(kpis));
        var day = Assert.Single(report.Days);
        Assert.Equal(Tomorrow, day.Date);
        Assert.True(day.Capacity > 0);
        Assert.True(day.Booked >= 1);
        Assert.True(report.Totals.Appointments >= 1);

        Assert.Equal(HttpStatusCode.BadRequest, (await planner.GetAsync($"{Terminal}/kpis?from={Tomorrow:yyyy-MM-dd}&to={Tomorrow.AddDays(-1):yyyy-MM-dd}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await planner.GetAsync($"{Terminal}/kpis?from={Tomorrow:yyyy-MM-dd}&to={Tomorrow.AddDays(100):yyyy-MM-dd}")).StatusCode);
    }
}
