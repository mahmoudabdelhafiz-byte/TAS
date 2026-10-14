using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Tas.Infrastructure;

namespace Tas.Api.Tests;

/// <summary>Endpoints the web portal needs: terminals to choose from, own message language, pending invitations.</summary>
public sealed class PortalSupportApiTests(TasApiFactory factory) : ApiTestBase(factory)
{
    [Fact]
    public async Task Terminals_are_listed_per_role()
    {
        if (!Enabled) return;

        var forCompany = await Read<List<JsonElement>>(await (await As(DemoDataSeeder.DispatcherEmail)).GetAsync("/api/terminals"));
        var terminal = Assert.Single(forCompany);
        Assert.Equal(Demo.TerminalId, terminal.GetProperty("terminalId").GetInt32());
        Assert.Equal("Asia/Riyadh", terminal.GetProperty("timeZone").GetString());
        Assert.True(terminal.GetProperty("bookingHorizonDays").GetInt32() > 0);

        var forStaff = await Read<List<JsonElement>>(await (await As(DemoDataSeeder.PlannerEmail)).GetAsync("/api/terminals"));
        Assert.Equal(Demo.TerminalId, Assert.Single(forStaff).GetProperty("terminalId").GetInt32());

        Assert.Equal(HttpStatusCode.Unauthorized, (await Factory.CreateClient().GetAsync("/api/terminals")).StatusCode);
    }

    [Fact]
    public async Task Users_set_their_own_message_language()
    {
        if (!Enabled) return;
        var planner = await As(DemoDataSeeder.PlannerEmail);

        var saved = await planner.PutAsJsonAsync("/api/auth/me/language", new { language = "UR" });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal("ur", (await Read<JsonElement>(saved)).GetProperty("preferredLanguage").GetString());
        Assert.Equal("ur", (await Read<JsonElement>(await planner.GetAsync("/api/auth/me"))).GetProperty("preferredLanguage").GetString());

        Assert.Equal(HttpStatusCode.BadRequest, (await planner.PutAsJsonAsync("/api/auth/me/language", new { language = "fr" })).StatusCode);
    }

    [Fact]
    public async Task The_user_list_shows_who_has_not_set_a_password_yet()
    {
        if (!Enabled) return;
        var admin = await As(DemoDataSeeder.TerminalAdminEmail);
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/users",
            new { email = "pending@demo-terminal.example", fullName = "Pending", role = "GateClerk", terminalId = Demo.TerminalId })).StatusCode);

        var users = await Read<List<JsonElement>>(await admin.GetAsync("/api/users"));
        Assert.False(users.Single(u => u.GetProperty("email").GetString() == "pending@demo-terminal.example").GetProperty("passwordSet").GetBoolean());
        Assert.True(users.Single(u => u.GetProperty("email").GetString() == DemoDataSeeder.PlannerEmail).GetProperty("passwordSet").GetBoolean());
    }
}
