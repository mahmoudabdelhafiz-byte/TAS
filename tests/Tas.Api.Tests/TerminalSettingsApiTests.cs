using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Tas.Infrastructure;
using Tas.Infrastructure.Ops;

namespace Tas.Api.Tests;

/// <summary>One installation, settings per terminal: the TOS is chosen per terminal code, and status shows each terminal's set-up.</summary>
public sealed class TerminalSettingsApiTests(TasApiFactory factory) : ApiTestBase(factory)
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
        var appointment = await Read<Tas.Application.Booking.AppointmentDto>(booked);
        var check = Assert.Single(appointment.Items).Checks.Single(c => c.Type == Tas.Domain.Booking.CheckType.TosUnavailable);
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
}
