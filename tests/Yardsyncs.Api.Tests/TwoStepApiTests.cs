using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Yardsyncs.Application.Auth;
using Yardsyncs.Infrastructure;
using Yardsyncs.Infrastructure.Auth;

namespace Yardsyncs.Api.Tests;

/// <summary>An installation as it is shipped: admins must use two-step sign-in.</summary>
public sealed class TwoStepRequiredFactory : YardsyncsApiFactory
{
    protected override void Extra(IWebHostBuilder builder) => builder.UseSetting("Auth:AdminTwoStep", "Required");
}

public sealed class TwoStepApiTests(TwoStepRequiredFactory factory) : ApiTestBase(factory), IClassFixture<TwoStepRequiredFactory>
{
    private HttpClient Bearer(string token)
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static string CodeNow(byte[] secret, int ahead = 0) => Totp.Code(secret, Totp.Step(DateTimeOffset.UtcNow) + ahead);

    [Fact]
    public async Task An_admin_sets_up_two_step_sign_in_before_anything_else_then_signs_in_with_a_code()
    {
        if (!Enabled) return;
        var settings = $"{Terminal}/settings";

        // The password alone: signed in, but only to set it up.
        var first = await Login(DemoDataSeeder.TerminalAdminEmail);
        Assert.True(first.User.TwoStepSetupRequired);
        var admin = Bearer(first.AccessToken);
        var blocked = await admin.GetAsync(settings);
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        Assert.Contains("twoStepSetupRequired", await blocked.Content.ReadAsStringAsync());

        var setup = (await Read<AdminResponse<TwoStepSetupView>>(await admin.PostAsync("/api/auth/two-step/setup", null))).Data;
        Assert.StartsWith("otpauth://totp/", setup.Uri);
        var secret = Totp.FromBase32(setup.Secret);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/auth/two-step/confirm", new { code = "000000" == CodeNow(secret) ? "111111" : "000000" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/auth/two-step/confirm", new { code = CodeNow(secret) })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(settings)).StatusCode); // at once, same session

        // Next sign-in: password, then the code.
        var password = await Factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email = DemoDataSeeder.TerminalAdminEmail, password = Password });
        Assert.Equal(HttpStatusCode.OK, password.StatusCode);
        using var step = JsonDocument.Parse(await password.Content.ReadAsStringAsync());
        Assert.True(step.RootElement.GetProperty("secondStep").GetBoolean());
        Assert.False(step.RootElement.TryGetProperty("accessToken", out _));
        var challenge = step.RootElement.GetProperty("challenge").GetString();

        Assert.Equal(HttpStatusCode.Unauthorized, (await Factory.CreateClient().PostAsJsonAsync("/api/auth/login/code", new { challenge, code = "12345" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Factory.CreateClient().PostAsJsonAsync("/api/auth/login/code", new { challenge = "forged.ticket", code = CodeNow(secret, 1) })).StatusCode);
        var signedIn = await Factory.CreateClient().PostAsJsonAsync("/api/auth/login/code", new { challenge, code = CodeNow(secret, 1) });
        Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
        var tokens = await Read<TokenResponse>(signedIn);
        Assert.True(tokens.User.TwoStepEnabled);
        Assert.False(tokens.User.TwoStepSetupRequired);
        Assert.Equal(HttpStatusCode.OK, (await Bearer(tokens.AccessToken).GetAsync(settings)).StatusCode);

        // The same code can't sign in twice; admins can't turn it off themselves.
        Assert.Equal(HttpStatusCode.Unauthorized, (await Factory.CreateClient().PostAsJsonAsync("/api/auth/login/code", new { challenge, code = CodeNow(secret, 1) })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Bearer(tokens.AccessToken).PostAsJsonAsync("/api/auth/two-step/disable", new { password = Password, code = CodeNow(secret) })).StatusCode);
    }

    [Fact]
    public async Task Others_may_use_it_and_an_admin_resets_it_for_a_lost_phone()
    {
        if (!Enabled) return;
        // Planners aren't required to.
        var planner = await Login(DemoDataSeeder.PlannerEmail);
        Assert.False(planner.User.TwoStepSetupRequired);
        var client = Bearer(planner.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{Terminal}/settings")).StatusCode);
        var secret = Totp.FromBase32((await Read<AdminResponse<TwoStepSetupView>>(await client.PostAsync("/api/auth/two-step/setup", null))).Data.Secret);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/two-step/confirm", new { code = CodeNow(secret) })).StatusCode);

        // The planner's phone is lost: a terminal admin resets it (the admin first sets up their own).
        var adminLogin = await Login(DemoDataSeeder.TerminalAdminEmail);
        var admin = Bearer(adminLogin.AccessToken);
        var adminSecret = Totp.FromBase32((await Read<AdminResponse<TwoStepSetupView>>(await admin.PostAsync("/api/auth/two-step/setup", null))).Data.Secret);
        await admin.PostAsJsonAsync("/api/auth/two-step/confirm", new { code = CodeNow(adminSecret) });
        var users = await Read<List<JsonElement>>(await admin.GetAsync($"/api/users?terminalId={Demo.TerminalId}"));
        var plannerId = users.Single(u => u.GetProperty("email").GetString() == DemoDataSeeder.PlannerEmail).GetProperty("userId").GetInt32();
        Assert.True(users.Single(u => u.GetProperty("userId").GetInt32() == plannerId).GetProperty("twoStepOn").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/users/{plannerId}/two-step/reset", new { reason = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"/api/users/{plannerId}/two-step/reset", new { reason = "Phone lost" })).StatusCode);

        // Back to the password alone, and the old session is gone.
        var again = await Factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email = DemoDataSeeder.PlannerEmail, password = Password });
        Assert.NotEmpty((await Read<TokenResponse>(again)).AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Factory.CreateClient().PostAsJsonAsync("/api/auth/refresh", new { refreshToken = planner.RefreshToken })).StatusCode);
    }
}
