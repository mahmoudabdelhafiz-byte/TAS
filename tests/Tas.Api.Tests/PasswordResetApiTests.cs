using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Tas.Infrastructure;
using Tas.Infrastructure.Notifications;

namespace Tas.Api.Tests;

/// <summary>Forgotten password and invitations through the API (the rules are covered by the database tests).</summary>
public sealed class PasswordResetApiTests(TasApiFactory factory) : ApiTestBase(factory)
{
    private async Task SendQueuedAsync()
    {
        using var scope = Factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<NotificationSender>().RunAsync();
    }

    [Fact]
    public async Task Forgot_password_then_set_a_new_one_with_the_emailed_link()
    {
        if (!Enabled) return;
        var client = Factory.CreateClient();
        const string email = DemoDataSeeder.PlannerEmail;

        // Same answer whether or not the account exists.
        var unknown = await client.PostAsJsonAsync("/api/auth/forgot-password", new { email = "nobody@nowhere.example" });
        var known = await client.PostAsJsonAsync("/api/auth/forgot-password", new { email });
        Assert.Equal(HttpStatusCode.Accepted, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, known.StatusCode);
        Assert.Equal(await unknown.Content.ReadAsStringAsync(), await known.Content.ReadAsStringAsync());

        await SendQueuedAsync();
        Assert.Contains("https://portal.test/reset-password#token=", Factory.Emails.Sent.Last(m => m.To == email).Message.Body);
        var token = Factory.Emails.LastToken(email);

        var link = await client.PostAsJsonAsync("/api/auth/password-link", new { token });
        Assert.Equal(HttpStatusCode.OK, link.StatusCode);
        var info = await Read<JsonElement>(link);
        Assert.Equal("Reset", info.GetProperty("purpose").GetString());
        Assert.Equal(email, info.GetProperty("email").GetString());

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/auth/reset-password", new { token, newPassword = "weak" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/auth/reset-password", new { token, newPassword = "Quay-Crane-2026" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/auth/reset-password", new { token, newPassword = "Quay-Again-2026" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/auth/password-link", new { token })).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginRaw(email, Password)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await LoginRaw(email, "Quay-Crane-2026")).StatusCode);
    }

    [Fact]
    public async Task Admins_invite_users_who_then_set_their_own_password()
    {
        if (!Enabled) return;
        var admin = await As(DemoDataSeeder.TerminalAdminEmail);
        const string email = "gate7@demo-terminal.example";

        var created = await admin.PostAsJsonAsync("/api/users",
            new { email, fullName = "Gate Clerk 7", role = "GateClerk", terminalId = Demo.TerminalId, preferredLanguage = "en" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await Read<JsonElement>(created);
        Assert.True(body.GetProperty("invited").GetBoolean());
        var userId = body.GetProperty("userId").GetInt32();

        await SendQueuedAsync();
        var invitation = Factory.Emails.Sent.Last(m => m.To == email).Message;
        Assert.Contains("https://portal.test/accept-invite#token=", invitation.Body);
        Assert.Contains("Demo Multipurpose Terminal", invitation.Subject);

        // Resend: new link, old one dead.
        var first = Factory.Emails.LastToken(email);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/users/{userId}/invite", null)).StatusCode);
        await SendQueuedAsync();
        var second = Factory.Emails.LastToken(email);
        Assert.Equal(HttpStatusCode.BadRequest, (await Factory.CreateClient().PostAsJsonAsync("/api/auth/password-link", new { token = first })).StatusCode);

        var accept = await Factory.CreateClient().PostAsJsonAsync("/api/auth/reset-password", new { token = second, newPassword = "Gate-Seven-2026" });
        Assert.Equal(HttpStatusCode.NoContent, accept.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await LoginRaw(email, "Gate-Seven-2026")).StatusCode);

        // Already has a password; and companies can't invite terminal staff.
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync($"/api/users/{userId}/invite", null)).StatusCode);
        var dispatcher = await As(DemoDataSeeder.DispatcherEmail);
        Assert.Equal(HttpStatusCode.NotFound, (await dispatcher.PostAsync($"/api/users/{userId}/invite", null)).StatusCode);
    }
}
