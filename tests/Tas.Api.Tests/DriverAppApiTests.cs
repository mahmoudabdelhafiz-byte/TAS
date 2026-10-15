using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tas.Infrastructure;
using Tas.Infrastructure.Persistence;
using Tas.Infrastructure.DriverApp;

namespace Tas.Api.Tests;

/// <summary>Driver app: sign-in with a code, my appointments, start trip, one phone at a time.</summary>
public sealed class DriverAppApiTests(TasApiFactory factory) : ApiTestBase(factory)
{
    private const string Abdullah = "0500000001"; // demo driver, +966500000001

    private async Task<string?> StartAsync(string mobile, string installId)
    {
        var response = await Factory.CreateClient().PostAsJsonAsync("/api/driver/login/start", new { mobile, installId });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("testCode").GetString();
    }

    private async Task<HttpClient> SignInAsync(string installId)
    {
        var code = await StartAsync(Abdullah, installId);
        var response = await Factory.CreateClient().PostAsJsonAsync("/api/driver/login/verify",
            new { mobile = Abdullah, installId, code, platform = "android", appVersion = "0.1.0" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var session = await Read<DriverSession>(response);
        Assert.Equal("Abdullah Al-Harbi", session.Driver.FullName);
        Assert.Equal("+966500000001", session.Driver.Mobile);

        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
        return client;
    }

    [Fact]
    public async Task Sign_in_sees_own_appointments_and_starts_the_trip()
    {
        if (!Enabled) return;

        // Al Noor books a truck with Abdullah as the driver, two days ahead (too early to start the trip).
        long slotId;
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TasDbContext>();
            var start = At(Tomorrow.AddDays(1), 10).ToUniversalTime();
            slotId = await db.Slots.Where(x => x.TerminalId == Demo.TerminalId && x.StartAt == start).Select(x => x.SlotId).SingleAsync();
        }
        var alNoor = await As(DemoDataSeeder.DispatcherEmail);
        var booked = await alNoor.PostAsJsonAsync(Appointments, new
        {
            slotId,
            driverId = Demo.DriverIds[0],
            items = new[] { new { category = "IMP_PICKUP", containerNo = "MSCU1234566" } },
        });
        Assert.Equal(HttpStatusCode.Created, booked.StatusCode);
        var number = (await Read<Tas.Application.Booking.AppointmentDto>(booked)).AppointmentNo;

        var driver = await SignInAsync("install-phone-0001");

        var list = await Read<List<DriverAppointment>>(await driver.GetAsync("/api/driver/appointments"));
        var mine = Assert.Single(list);
        Assert.Equal(number, mine.AppointmentNo);
        Assert.Equal("استلام حاوية وارد", Assert.Single(mine.Moves).CategoryAr);
        Assert.NotNull(mine.GateCode);

        Assert.Equal(HttpStatusCode.OK, (await driver.GetAsync($"/api/driver/appointments/{number}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await driver.GetAsync("/api/driver/appointments/DEMO-000000-NOPE1")).StatusCode);

        // Too early to start the trip (allowed from 12 hours before the slot).
        var early = await driver.PostAsync($"/api/driver/appointments/{number}/start-trip", null);
        Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
        Assert.Contains("Too early", await early.Content.ReadAsStringAsync());

        // Driver tokens don't open staff endpoints, and staff tokens don't open driver endpoints.
        Assert.Equal(HttpStatusCode.Unauthorized, (await driver.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await alNoor.GetAsync("/api/driver/appointments")).StatusCode);
    }

    [Fact]
    public async Task Codes_are_single_use_limited_and_one_phone_at_a_time()
    {
        if (!Enabled) return;

        // Unknown numbers get the same answer (nothing is sent).
        Assert.Null(await StartAsync("0599999999", "install-unknown-01"));

        // Wrong code, then the right one; the code can't be used twice.
        var code = await StartAsync(Abdullah, "install-phone-0001");
        var anon = Factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/api/driver/login/verify",
            new { mobile = Abdullah, installId = "install-phone-0001", code = code == "000000" ? "111111" : "000000" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/api/driver/login/verify",
            new { mobile = Abdullah, installId = "install-other-0001", code })).StatusCode); // another phone can't use it
        Assert.Equal(HttpStatusCode.OK, (await anon.PostAsJsonAsync("/api/driver/login/verify",
            new { mobile = Abdullah, installId = "install-phone-0001", code })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/api/driver/login/verify",
            new { mobile = Abdullah, installId = "install-phone-0001", code })).StatusCode);

        // Signing in on a second phone signs the first one out (2 more codes: 3 in 15 minutes is the limit).
        var first = await SignInAsync("install-phone-0002");
        Assert.Equal(HttpStatusCode.OK, (await first.GetAsync("/api/driver/me")).StatusCode);
        var second = await SignInAsync("install-phone-0003");
        Assert.Equal(HttpStatusCode.OK, (await second.GetAsync("/api/driver/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await first.GetAsync("/api/driver/me")).StatusCode);

        // Fourth code within 15 minutes is refused.
        Assert.Equal(HttpStatusCode.TooManyRequests, (await anon.PostAsJsonAsync("/api/driver/login/start",
            new { mobile = Abdullah, installId = "install-phone-0004" })).StatusCode);

        // Logging out ends the session.
        Assert.Equal(HttpStatusCode.NoContent, (await second.PostAsync("/api/driver/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await second.GetAsync("/api/driver/me")).StatusCode);
    }

    [Fact]
    public async Task Blocked_drivers_cannot_sign_in()
    {
        if (!Enabled) return;

        var admin = await As(DemoDataSeeder.TerminalAdminEmail);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync(
            $"/api/companies/{Demo.CompanyId}/drivers/{Demo.DriverIds[0]}/block", new { blocked = true, reason = "Unsafe driving" })).StatusCode);

        var code = await StartAsync(Abdullah, "install-phone-0001");
        Assert.Null(code); // treated like an unknown number
        Assert.Equal(HttpStatusCode.Unauthorized, (await Factory.CreateClient().PostAsJsonAsync("/api/driver/login/verify",
            new { mobile = Abdullah, installId = "install-phone-0001", code = "123456" })).StatusCode);
    }
}
