using System.Net;
using System.Net.Http.Json;
using Tas.Infrastructure;
using Tas.Infrastructure.Ops;

namespace Tas.Api.Tests;

/// <summary>Language packs: the test installation has a French pack (tests/Tas.Api.Tests/languages/fr.json).</summary>
public sealed class LanguageApiTests(TasApiFactory factory) : ApiTestBase(factory)
{
    private sealed record LanguageRow(string Code, string Name, string Direction, bool Portal, bool App, bool Messages, bool FromPack);

    private sealed record PackRow(string Code, string Name, Dictionary<string, string> Portal, Dictionary<string, string> App);

    [Fact]
    public async Task A_pack_adds_a_language_for_the_portal_the_app_and_messages()
    {
        if (!Enabled) return;

        // Public: the sign-in page and the drivers' app read them before anyone signs in.
        var anonymous = Factory.CreateClient();
        var languages = await Read<List<LanguageRow>>(await anonymous.GetAsync("/api/languages"));
        Assert.Equal(new[] { "en", "ar", "ur", "hi", "fr" }, languages.Select(l => l.Code));
        Assert.Equal(new LanguageRow("fr", "Français", "ltr", true, true, true, true), languages[^1]);
        Assert.False(languages.Single(l => l.Code == "ur").Portal);

        var fr = await Read<PackRow>(await anonymous.GetAsync("/api/languages/fr"));
        Assert.Equal("Réserver un camion", fr.Portal["nav.book"]);
        Assert.Equal("Se déconnecter", fr.App["signOut"]);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync("/api/languages/ar")).StatusCode); // built in: no pack here

        // A driver can get messages in French; a language nobody added is refused.
        var alNoor = await As(DemoDataSeeder.DispatcherEmail);
        var drivers = $"/api/companies/{Demo.CompanyId}/drivers";
        Assert.Equal(HttpStatusCode.Created, (await alNoor.PostAsJsonAsync(drivers,
            new { fullName = "Jean Martin", idNumber = "2098765432", mobile = "0551234567", preferredLanguage = "fr" })).StatusCode);
        var refused = await alNoor.PostAsJsonAsync(drivers,
            new { fullName = "Hans Weber", idNumber = "2098765433", mobile = "0551234568", preferredLanguage = "de" });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("fr", await refused.Content.ReadAsStringAsync());

        var status = await Read<OpsStatus>(await (await As(DemoDataSeeder.TerminalAdminEmail)).GetAsync("/api/ops/status"));
        Assert.Equal("fr Français: portal 2, app 1, messages 1", Assert.Single(status.LanguagePacks!));
    }
}
