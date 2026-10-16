using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Yardsyncs.Infrastructure;
using Yardsyncs.Infrastructure.Ops;

namespace Yardsyncs.Api.Tests;

/// <summary>One installation, settings per terminal: the TOS is chosen per terminal code, and status shows each terminal's set-up.</summary>
public sealed class TerminalSettingsApiTests(YardsyncsApiFactory factory) : ApiTestBase(factory)
{
    [Fact]
    public async Task Each_terminal_uses_its_own_TOS_and_sender_names()
    {
        if (!Enabled) return;

        // The demo terminal switched to N4, which isn't connected here: bookings go ahead with a warning (Tos:OnUnavailable=Warn).
        using var n4 = Factory.WithWebHostBuilder(b => b
            .UseSetting("Terminals:DEMO:Tos", "N4")
            .UseSetting("Terminals:DEMO:Brand", "Demo Port")
            .UseSetting("Terminals:DEMO:Notifications:Unifonic:SenderId", "DemoPort"));
        var login = await n4.CreateClient().PostAsJsonAsync("/api/auth/login", new { email = DemoDataSeeder.TerminalAdminEmail, password = Password });
        var tokens = await Read<TokenResponse>(login);
        var admin = n4.CreateClient();
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var booked = await admin.PostAsJsonAsync(Appointments, ImportBooking(companyId: Demo.CompanyId));
        Assert.Equal(HttpStatusCode.Created, booked.StatusCode);
        var appointment = await Read<Yardsyncs.Application.Booking.AppointmentDto>(booked);
        var check = Assert.Single(appointment.Items).Checks.Single(c => c.Type == Yardsyncs.Domain.Booking.CheckType.TosUnavailable);
        Assert.Equal("N4", check.Source);

        var status = await Read<OpsStatus>(await admin.GetAsync("/api/ops/status"));
        Assert.Equal("DEMO: N4", status.Tos);
        var demo = Assert.Single(status.Terminals);
        Assert.Equal("N4", demo.Tos);
        Assert.Equal("Demo Port", demo.Brand);
        Assert.Equal("DemoPort", demo.SmsSender);
        Assert.True(demo.OwnMessageSettings);

        // The same installation without the terminal's settings: the demo TOS, the default name.
        var plain = await Read<OpsStatus>(await (await As(DemoDataSeeder.TerminalAdminEmail)).GetAsync("/api/ops/status"));
        Assert.Equal("DEMO: Fake", plain.Tos);
        Assert.Equal("Yardsyncs", Assert.Single(plain.Terminals).Brand);
    }

    private sealed record CountryRow(string Code, string DialCode, string MobileExample, string IdNameEn, string Currency);

    private sealed record HolidayRow(DateOnly Date, string NameEn, string NameAr, bool Lunar);

    private sealed record TerminalRow(string Code, string Country, string Currency);

    [Fact]
    public async Task Each_terminal_has_the_holidays_of_its_country()
    {
        if (!Enabled) return;

        // Public, so the drivers' app can show how to type the mobile number before sign-in.
        var country = await Read<CountryRow>(await Factory.CreateClient().GetAsync("/api/country"));
        Assert.Equal(("SA", "+966", "05XXXXXXXX", "SAR"), (country.Code, country.DialCode, country.MobileExample, country.Currency));

        var dispatcher = await As(DemoDataSeeder.DispatcherEmail);
        var saudi = await Read<List<HolidayRow>>(await dispatcher.GetAsync($"{Terminal}/holidays?year=2026"));
        Assert.Contains(saudi, h => h.Date == new DateOnly(2026, 9, 23) && h.NameEn == "National Day");
        Assert.Equal(HttpStatusCode.BadRequest, (await dispatcher.GetAsync($"{Terminal}/holidays?year=1990")).StatusCode);
        var demo = Assert.Single(await Read<List<TerminalRow>>(await dispatcher.GetAsync("/api/terminals")));
        Assert.Equal(("SA", "SAR"), (demo.Country, demo.Currency));

        // The same terminal set up as one in the Emirates: its holidays and currency, the installation's mobile rules.
        using var emirates = Factory.WithWebHostBuilder(b => b.UseSetting("Terminals:DEMO:Country", "AE"));
        var login = await emirates.CreateClient().PostAsJsonAsync("/api/auth/login", new { email = DemoDataSeeder.DispatcherEmail, password = Password });
        var client = emirates.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await Read<TokenResponse>(login)).AccessToken);
        var uae = await Read<List<HolidayRow>>(await client.GetAsync($"{Terminal}/holidays?year=2026"));
        Assert.Contains(uae, h => h.Date == new DateOnly(2026, 12, 2));
        Assert.DoesNotContain(uae, h => h.NameEn == "Founding Day");
        Assert.Equal("AED", Assert.Single(await Read<List<TerminalRow>>(await client.GetAsync("/api/terminals"))).Currency);
        Assert.Equal("SA", (await Read<CountryRow>(await client.GetAsync("/api/country"))).Code);
    }
}
