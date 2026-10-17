using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Yardsyncs.Infrastructure;

namespace Yardsyncs.Api.Tests;

/// <summary>The portal's session: the refresh token in an HttpOnly cookie, the access token only in the page's memory.</summary>
public sealed class PortalSessionApiTests(YardsyncsApiFactory factory) : ApiTestBase(factory)
{
    private HttpClient Portal()
    {
        var client = Factory.CreateClient(); // keeps cookies, as a browser does
        client.DefaultRequestHeaders.Add(PortalSession.Header, "cookie");
        return client;
    }

    [Fact]
    public async Task The_refresh_token_stays_in_an_httponly_cookie()
    {
        if (!Enabled) return;
        var portal = Portal();
        var login = await portal.PostAsJsonAsync("/api/auth/login", new { email = DemoDataSeeder.PlannerEmail, password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var tokens = (await login.Content.ReadFromJsonAsync<TokenResponse>(Json))!;
        Assert.Equal("", tokens.RefreshToken);                 // never in the page
        Assert.NotEmpty(tokens.AccessToken);
        var cookie = Assert.Single(login.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith($"{PortalSession.Cookie}=", cookie);
        Assert.Contains("httponly", cookie.ToLowerInvariant());
        Assert.Contains("samesite=strict", cookie.ToLowerInvariant());
        Assert.Contains("path=/api/auth", cookie.ToLowerInvariant());
        var oldValue = cookie.Split(';')[0][(PortalSession.Cookie.Length + 1)..];

        // A reload: no access token in memory, the cookie gets a new one.
        var refreshed = await portal.PostAsJsonAsync("/api/auth/refresh", new { });
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        var access = (await refreshed.Content.ReadFromJsonAsync<TokenResponse>(Json))!.AccessToken;
        var me = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        me.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        Assert.Equal(HttpStatusCode.OK, (await portal.SendAsync(me)).StatusCode);

        // Another tab still holding the cookie it just rotated: refused, without signing everyone out.
        var otherTab = Factory.CreateClient(new() { HandleCookies = false });
        var stale = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh") { Content = JsonContent.Create(new { }) };
        stale.Headers.Add(PortalSession.Header, "cookie");
        stale.Headers.Add("Cookie", $"{PortalSession.Cookie}={oldValue}");
        Assert.Equal(HttpStatusCode.Unauthorized, (await otherTab.SendAsync(stale)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await portal.PostAsJsonAsync("/api/auth/refresh", new { })).StatusCode);

        // Without the portal's header the cookie isn't used (another site can't refresh with it).
        var plain = Factory.CreateClient();
        var noHeader = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh") { Content = JsonContent.Create(new { }) };
        noHeader.Headers.Add("Cookie", $"{PortalSession.Cookie}={oldValue}");
        Assert.Equal(HttpStatusCode.Unauthorized, (await plain.SendAsync(noHeader)).StatusCode);

        // Signing out ends the session and clears the cookie.
        var logout = await portal.PostAsJsonAsync("/api/auth/logout", new { });
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Contains(logout.Headers.GetValues("Set-Cookie"), c => c.StartsWith($"{PortalSession.Cookie}=;"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await portal.PostAsJsonAsync("/api/auth/refresh", new { })).StatusCode);
    }

    [Fact]
    public async Task Api_clients_keep_the_tokens_in_the_body()
    {
        if (!Enabled) return;
        var tokens = await Login(DemoDataSeeder.PlannerEmail);
        Assert.NotEmpty(tokens.RefreshToken);
        var refreshed = await Factory.CreateClient().PostAsJsonAsync("/api/auth/refresh", new { refreshToken = tokens.RefreshToken });
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        Assert.NotEmpty((await refreshed.Content.ReadFromJsonAsync<TokenResponse>(Json))!.RefreshToken);
    }
}
