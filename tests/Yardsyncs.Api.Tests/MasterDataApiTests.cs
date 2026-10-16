using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yardsyncs.Application.MasterData;
using Yardsyncs.Infrastructure;
using Yardsyncs.Infrastructure.Persistence;

namespace Yardsyncs.Api.Tests;

/// <summary>Companies, fleet, categories, cargo releases and users through the API.</summary>
public sealed class MasterDataApiTests(YardsyncsApiFactory factory) : ApiTestBase(factory)
{
    /// <summary>A 10:00 slot on the next Sunday–Thursday (weekend slots take no breakbulk in the demo).</summary>
    private async Task<long> WeekdaySlotAsync()
    {
        var day = Tomorrow;
        while (day.DayOfWeek is DayOfWeek.Friday or DayOfWeek.Saturday)
            day = day.AddDays(1);

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<YardsyncsDbContext>();
        var start = At(day, 10).ToUniversalTime();
        return await db.Slots.Where(s => s.TerminalId == Demo.TerminalId && s.StartAt == start).Select(s => s.SlotId).SingleAsync();
    }

    private static object Booking(long slotId, string category, string? container = null, string? cargoRef = null, int? truckId = null) => new
    {
        slotId,
        truckId,
        items = new[] { new { category, containerNo = container, cargoRef } },
    };

    [Fact]
    public async Task Suspended_companies_cannot_book_and_companies_only_see_themselves()
    {
        if (!Enabled) return;

        var admin = await As(DemoDataSeeder.TerminalAdminEmail);
        var planner = await As(DemoDataSeeder.PlannerEmail);
        var gulf = await As(DemoDataSeeder.OtherDispatcherEmail);
        var suspend = $"/api/companies/{Demo.OtherCompanyId}/status";

        Assert.Equal(HttpStatusCode.Forbidden, (await planner.PostAsJsonAsync(suspend, new { status = "Suspended", reason = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync(suspend, new { status = "Suspended" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync(suspend, new { status = "Suspended", reason = "Unpaid invoices" })).StatusCode);

        var refused = await gulf.PostAsJsonAsync(Appointments, ImportBooking(container: "MAEU4001110"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Contains("suspended", await refused.Content.ReadAsStringAsync());

        var visible = await Read<List<CompanyView>>(await gulf.GetAsync("/api/companies"));
        Assert.Equal(Demo.OtherCompanyId, Assert.Single(visible).CompanyId);
        Assert.Equal(HttpStatusCode.NotFound, (await gulf.GetAsync($"/api/companies/{Demo.CompanyId}")).StatusCode);
        Assert.True((await Read<List<CompanyView>>(await planner.GetAsync("/api/companies?search=noor"))).Count == 1);

        var created = await admin.PostAsJsonAsync("/api/companies", new { name = "Red Sea Logistics", crNumber = "4030999999", companyType = "Forwarder" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,
            (await admin.PostAsJsonAsync("/api/companies", new { name = "Copy", crNumber = "4030999999", companyType = "Forwarder" })).StatusCode);
    }

    [Fact]
    public async Task Fleet_with_arabic_plates_and_terminal_blocking()
    {
        if (!Enabled) return;

        var alNoor = await As(DemoDataSeeder.DispatcherEmail);
        var gulf = await As(DemoDataSeeder.OtherDispatcherEmail);
        var admin = await As(DemoDataSeeder.TerminalAdminEmail);
        var trucks = $"/api/companies/{Demo.CompanyId}/trucks";

        var added = await alNoor.PostAsJsonAsync(trucks, new { plateNumber = "أ ب ح ٩٨٧٦" });
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        var truck = (await Read<Yardsyncs.Api.AdminResponse<TruckView>>(added)).Data;
        Assert.Equal("9876ABJ", truck.PlateNormalized);

        // The same truck typed the Latin way by another company is the same plate.
        Assert.Equal(HttpStatusCode.Conflict,
            (await gulf.PostAsJsonAsync($"/api/companies/{Demo.OtherCompanyId}/trucks", new { plateNumber = "9876 ABJ" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await gulf.PostAsJsonAsync(trucks, new { plateNumber = "1111 XYZ" })).StatusCode);

        var driver = await alNoor.PostAsJsonAsync($"/api/companies/{Demo.CompanyId}/drivers",
            new { fullName = "Saeed Al-Qahtani", idNumber = "1098765432", mobile = "050 999 8877", preferredLanguage = "ar" });
        Assert.Equal(HttpStatusCode.Created, driver.StatusCode);
        Assert.Equal("+966509998877", (await Read<Yardsyncs.Api.AdminResponse<DriverView>>(driver)).Data.Mobile);

        // Blocking is a terminal decision, and a blocked truck cannot be booked.
        var block = $"{trucks}/{truck.TruckId}/block";
        Assert.Equal(HttpStatusCode.Forbidden, (await alNoor.PostAsJsonAsync(block, new { blocked = true, reason = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync(block, new { blocked = true, reason = "Safety inspection failed" })).StatusCode);

        var booking = await alNoor.PostAsJsonAsync(Appointments, Booking(SlotId, "IMP_PICKUP", container: "MSCU1234566", truckId: truck.TruckId));
        Assert.Equal(HttpStatusCode.BadRequest, booking.StatusCode);
        Assert.Contains("blocked", await booking.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task New_category_is_bookable_at_once_and_deactivating_it_stops_bookings()
    {
        if (!Enabled) return;

        var admin = await As(DemoDataSeeder.TerminalAdminEmail);
        var planner = await As(DemoDataSeeder.PlannerEmail);
        var alNoor = await As(DemoDataSeeder.DispatcherEmail);
        var categories = $"{Terminal}/categories";
        var oog = new { code = "oog_pickup", nameEn = "Out-of-gauge pickup", nameAr = "استلام حاوية خارج المقاس", mode = "Container", sortOrder = 9 };

        Assert.Equal(HttpStatusCode.Forbidden, (await planner.PostAsJsonAsync(categories, oog)).StatusCode);
        var created = await admin.PostAsJsonAsync(categories, oog);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var category = (await Read<Yardsyncs.Api.AdminResponse<CategoryView>>(created)).Data;
        Assert.Equal("OOG_PICKUP", category.Code);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync(categories, oog)).StatusCode);

        var visible = await Read<List<CategoryView>>(await alNoor.GetAsync(categories));
        Assert.Contains(visible, c => c.Code == "OOG_PICKUP");
        Assert.Equal(HttpStatusCode.Created,
            (await alNoor.PostAsJsonAsync(Appointments, Booking(SlotId, "OOG_PICKUP", container: "MSCU1234566"))).StatusCode);

        var update = $"{categories}/{category.CategoryId}";
        Assert.Equal(HttpStatusCode.BadRequest,
            (await admin.PutAsJsonAsync(update, new { code = "OOG_DROP", nameEn = "x", nameAr = "y", mode = "Container" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await admin.PutAsJsonAsync(update, new { code = "OOG_PICKUP", nameEn = "OOG pickup", nameAr = "استلام", mode = "Container", isActive = false })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await alNoor.PostAsJsonAsync(Appointments, Booking(SlotId, "OOG_PICKUP", container: "MAEU4001110"))).StatusCode);
    }

    [Fact]
    public async Task Cargo_releases_limit_breakbulk_trips()
    {
        if (!Enabled) return;

        var planner = await As(DemoDataSeeder.PlannerEmail);
        var alNoor = await As(DemoDataSeeder.DispatcherEmail);
        var gulf = await As(DemoDataSeeder.OtherDispatcherEmail);
        var cargo = $"{Terminal}/cargo";

        Assert.Equal(HttpStatusCode.Forbidden, (await alNoor.PostAsJsonAsync(cargo, new { cargoRef = "BL-X", cargoType = "Breakbulk", totalTrips = 1 })).StatusCode);
        var created = await planner.PostAsJsonAsync(cargo, new
        {
            cargoRef = "bl-pipes-777", companyId = Demo.CompanyId, commodity = "Line pipes", cargoType = "Breakbulk", totalTrips = 2, totalTonnage = 60,
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var release = (await Read<Yardsyncs.Api.AdminResponse<CargoView>>(created)).Data;

        Assert.Contains(await Read<List<CargoView>>(await alNoor.GetAsync(cargo)), c => c.CargoRef == "BL-PIPES-777");
        Assert.DoesNotContain(await Read<List<CargoView>>(await gulf.GetAsync(cargo)), c => c.CargoRef == "BL-PIPES-777");

        var slot = await WeekdaySlotAsync();
        Assert.Equal(HttpStatusCode.Created, (await alNoor.PostAsJsonAsync(Appointments, Booking(slot, "BB_DELIVERY", cargoRef: "BL-PIPES-777"))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await alNoor.PostAsJsonAsync(Appointments, Booking(slot, "BB_DELIVERY", cargoRef: "BL-PIPES-777"))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await alNoor.PostAsJsonAsync(Appointments, Booking(slot, "BB_DELIVERY", cargoRef: "BL-PIPES-777"))).StatusCode);

        var shrink = await planner.PutAsJsonAsync($"{cargo}/{release.CargoAllocationId}", new
        {
            cargoRef = "BL-PIPES-777", companyId = Demo.CompanyId, cargoType = "Breakbulk", totalTrips = 1, totalTonnage = 60,
        });
        Assert.Equal(HttpStatusCode.BadRequest, shrink.StatusCode);
        Assert.Contains("already booked", await shrink.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Admins_manage_users_and_changes_take_effect_immediately()
    {
        if (!Enabled) return;

        var alNoorAdmin = await As(DemoDataSeeder.DispatcherEmail);
        var terminalAdmin = await As(DemoDataSeeder.TerminalAdminEmail);

        var created = await alNoorAdmin.PostAsJsonAsync("/api/users", new
        {
            email = "clerk@alnoor.example", fullName = "Booking clerk", role = "CompanyUser", companyId = Demo.CompanyId,
            password = "Booking-Desk-77", preferredLanguage = "en",
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var ownUsers = await Read<List<UserView>>(await alNoorAdmin.GetAsync("/api/users"));
        Assert.All(ownUsers, u => Assert.Equal(Demo.CompanyId, u.CompanyId));
        var clerk = ownUsers.Single(u => u.Email == "clerk@alnoor.example");

        var everyone = await Read<List<UserView>>(await terminalAdmin.GetAsync("/api/users"));
        Assert.Contains(everyone, u => u.Email == DemoDataSeeder.OtherDispatcherEmail);
        Assert.Contains(everyone, u => u.Email == DemoDataSeeder.PlannerEmail);

        // Other companies' users are invisible; nobody changes their own role.
        var gulfDispatcher = everyone.Single(u => u.Email == DemoDataSeeder.OtherDispatcherEmail);
        Assert.Equal(HttpStatusCode.NotFound, (await alNoorAdmin.PutAsJsonAsync($"/api/users/{gulfDispatcher.UserId}",
            new { fullName = "x", preferredLanguage = "en", role = "CompanyUser" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await alNoorAdmin.PutAsJsonAsync($"/api/users/{Demo.DispatcherUserId}",
            new { fullName = "Khalid", preferredLanguage = "en", role = "CompanyUser" })).StatusCode);

        // A role change invalidates the clerk's current token at once.
        var clerkClient = await As("clerk@alnoor.example", "Booking-Desk-77");
        Assert.Equal(HttpStatusCode.OK, (await clerkClient.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await alNoorAdmin.PutAsJsonAsync($"/api/users/{clerk.UserId}",
            new { fullName = "Senior clerk", mobile = "0501112233", preferredLanguage = "ar", role = "CompanyAdmin" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await clerkClient.GetAsync("/api/auth/me")).StatusCode);

        // Deactivation: needs a reason, blocks the current token and new logins.
        clerkClient = await As("clerk@alnoor.example", "Booking-Desk-77");
        Assert.Equal(HttpStatusCode.BadRequest, (await alNoorAdmin.PostAsJsonAsync($"/api/users/{clerk.UserId}/active", new { active = false })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await alNoorAdmin.PostAsJsonAsync($"/api/users/{clerk.UserId}/active", new { active = false, reason = "Left the company" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await clerkClient.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginRaw("clerk@alnoor.example", "Booking-Desk-77")).StatusCode);

        // Reactivate and reset the password.
        Assert.Equal(HttpStatusCode.OK, (await alNoorAdmin.PostAsJsonAsync($"/api/users/{clerk.UserId}/active", new { active = true })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await alNoorAdmin.PostAsJsonAsync($"/api/users/{clerk.UserId}/reset-password", new { newPassword = "Fresh-Start-2026" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginRaw("clerk@alnoor.example", "Booking-Desk-77")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await LoginRaw("clerk@alnoor.example", "Fresh-Start-2026")).StatusCode);

        // Company users can't list users at all.
        var plainUser = Factory.CreateClient();
        plainUser.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await Login(DemoDataSeeder.PlannerEmail)).AccessToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await plainUser.GetAsync("/api/users")).StatusCode);
    }

    [Fact]
    public async Task Terminal_list_shows_what_each_user_can_work_at()
    {
        if (!Enabled) return;

        Assert.Equal(HttpStatusCode.Unauthorized, (await Factory.CreateClient().GetAsync("/api/terminals")).StatusCode);

        var forCompany = await Read<List<TerminalView>>(await (await As(DemoDataSeeder.DispatcherEmail)).GetAsync("/api/terminals"));
        var demo = Assert.Single(forCompany, t => t.TerminalId == Demo.TerminalId);
        Assert.Equal(DemoDataSeeder.TerminalCode, demo.Code);
        Assert.Equal("Asia/Riyadh", demo.TimeZone);

        var forPlanner = await Read<List<TerminalView>>(await (await As(DemoDataSeeder.PlannerEmail)).GetAsync("/api/terminals"));
        Assert.Equal(Demo.TerminalId, Assert.Single(forPlanner).TerminalId);
    }
}
