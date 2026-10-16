using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tas.Infrastructure;
using Tas.Infrastructure.DriverApp;
using Tas.Infrastructure.Ops;
using Tas.Infrastructure.Persistence;

namespace Tas.Api.Tests;

/// <summary>Security review: personal data (PDPL) export, erasure and clean-up; sign-in code guessing; unsafe settings.</summary>
public sealed class PrivacyApiTests(TasApiFactory factory) : ApiTestBase(factory)
{
    private const string Abdullah = "0500000001";

    private string DriverUrl => $"/api/companies/{Demo.CompanyId}/drivers/{Demo.DriverIds[0]}";

    private async Task<string?> StartAsync(string installId)
    {
        var response = await Factory.CreateClient().PostAsJsonAsync("/api/driver/login/start", new { mobile = Abdullah, installId });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("testCode").GetString();
    }

    private Task<HttpResponseMessage> VerifyAsync(string installId, string? code) =>
        Factory.CreateClient().PostAsJsonAsync("/api/driver/login/verify", new { mobile = Abdullah, installId, code, platform = "android" });

    /// <summary>Abdullah books, signs in, drives to the pre-gate (positions, estimate, area entries). Returns the appointment.</summary>
    private async Task<string> VisitWithPositionsAsync()
    {
        var alNoor = await As(DemoDataSeeder.DispatcherEmail);
        var booked = await alNoor.PostAsJsonAsync(Appointments, new
        {
            slotId = SlotId,
            driverId = Demo.DriverIds[0],
            items = new[] { new { category = "IMP_PICKUP", containerNo = "MSCU1234566" } },
        });
        Assert.Equal(HttpStatusCode.Created, booked.StatusCode);
        var number = (await Read<Tas.Application.Booking.AppointmentDto>(booked)).AppointmentNo;

        var code = await StartAsync("install-phone-0001");
        var session = await Read<DriverSession>(await VerifyAsync("install-phone-0001", code));
        var phone = Factory.CreateClient();
        phone.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TasDbContext>();
            await db.Appointments.Where(a => a.AppointmentNo == number)
                .ExecuteUpdateAsync(x => x.SetProperty(a => a.Status, Tas.Domain.Booking.AppointmentStatus.EnRoute));
        }
        var position = $"/api/driver/appointments/{number}/position";
        Assert.Equal(HttpStatusCode.OK, (await phone.PostAsJsonAsync(position, new { latitude = 21.30, longitude = 39.10, speed = 15.0 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await phone.PostAsJsonAsync(position, new { latitude = 21.4717, longitude = 39.1700 })).StatusCode);
        return number;
    }

    [Fact]
    public async Task A_driver_gets_a_copy_of_their_data_and_can_have_it_erased()
    {
        if (!Enabled) return;

        var number = await VisitWithPositionsAsync();
        var admin = await As(DemoDataSeeder.TerminalAdminEmail);

        var copy = await Read<PersonalDataExport>(await admin.GetAsync($"{DriverUrl}/personal-data"));
        Assert.Equal("Abdullah Al-Harbi", copy.Driver.FullName);
        Assert.Equal("+966500000001", copy.Driver.Mobile);
        Assert.Single(copy.Phones);
        Assert.Contains(copy.Visits, v => v.AppointmentNo == number && v.LastPosition is not null);
        Assert.Contains(copy.Positions, p => p.Kind == "ON_THE_WAY");
        Assert.Contains(copy.Positions, p => p.Kind == "AREA_ENTER");
        Assert.Contains("30 days", copy.Retention);

        // Only the company's own admin or terminal admins.
        var other = await As(DemoDataSeeder.OtherDispatcherEmail);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync($"{DriverUrl}/personal-data")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PostAsJsonAsync($"{DriverUrl}/erase", new { reason = "x" })).StatusCode);
        var alNoor = await As(DemoDataSeeder.DispatcherEmail);
        Assert.Equal(HttpStatusCode.OK, (await alNoor.GetAsync($"{DriverUrl}/personal-data")).StatusCode);

        // Not while a visit is under way, and not without a reason.
        var busy = await admin.PostAsJsonAsync($"{DriverUrl}/erase", new { reason = "Driver asked" });
        Assert.Equal(HttpStatusCode.Conflict, busy.StatusCode);
        Assert.Contains(number, await busy.Content.ReadAsStringAsync());
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TasDbContext>();
            await db.Appointments.Where(a => a.AppointmentNo == number)
                .ExecuteUpdateAsync(x => x.SetProperty(a => a.Status, Tas.Domain.Booking.AppointmentStatus.Completed));
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"{DriverUrl}/erase", new { reason = " " })).StatusCode);

        var erased = await admin.PostAsJsonAsync($"{DriverUrl}/erase", new { reason = "Driver asked" });
        Assert.Equal(HttpStatusCode.OK, erased.StatusCode);

        var after = await Read<PersonalDataExport>(await admin.GetAsync($"{DriverUrl}/personal-data"));
        Assert.Equal("Erased driver", after.Driver.FullName);
        Assert.Equal("", after.Driver.Mobile);
        Assert.Equal(PersonalDataService.Erased(Demo.DriverIds[0]), after.Driver.IdNumber);
        Assert.Empty(after.Positions);
        Assert.All(after.Visits, v => Assert.Null(v.LastPosition));
        Assert.All(after.Phones, p => Assert.False(p.IsActive));
        Assert.All(after.Messages, m => Assert.Null(m.Recipient));
        Assert.Contains(after.Visits, v => v.AppointmentNo == number); // the visit itself stays

        // The number no longer signs in, and erasing twice is refused.
        Assert.Null(await StartAsync("install-phone-0002"));
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"{DriverUrl}/erase", new { reason = "Driver asked" })).StatusCode);

        // The audit trail keeps that it happened, without the old values.
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TasDbContext>();
            var key = Demo.DriverIds[0].ToString();
            var trail = await db.AuditLogs.AsNoTracking().Where(a => a.EntityName == "driver" && a.EntityKey == key).ToListAsync();
            Assert.Contains(trail, a => a.Reason == "Personal data erased: Driver asked");
            Assert.All(trail, a => { Assert.Null(a.OldValues); Assert.Null(a.NewValues); });
        }
    }

    [Fact]
    public async Task Old_personal_data_is_cleaned_up()
    {
        if (!Enabled) return;

        var number = await VisitWithPositionsAsync();
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TasDbContext>();

        // Recent data stays.
        await scope.ServiceProvider.GetRequiredService<RetentionService>().RunAsync();
        var id = await db.Appointments.Where(a => a.AppointmentNo == number).Select(a => a.AppointmentId).SingleAsync();
        Assert.True(await db.AppointmentEtas.AnyAsync(e => e.AppointmentId == id && e.Latitude != null));

        // Forty days later the coordinates go; the visit, the estimates' times and the area entries stay.
        await db.Database.ExecuteSqlAsync($"UPDATE appointment SET last_position_at = last_position_at - interval '40 days' WHERE appointment_id = {id}");
        await db.Database.ExecuteSqlAsync($"UPDATE appointment_eta SET recorded_at = recorded_at - interval '40 days' WHERE appointment_id = {id}");
        await db.Database.ExecuteSqlAsync($"UPDATE geofence_event SET occurred_at = occurred_at - interval '40 days' WHERE appointment_id = {id}");
        await db.Database.ExecuteSqlAsync($"UPDATE driver_otp SET created_at = created_at - interval '2 days'");

        var run = await scope.ServiceProvider.GetRequiredService<RetentionService>().RunAsync();
        Assert.True(run.Removed["positions"] >= 3);
        Assert.True(run.Removed["signInCodes"] >= 1);
        Assert.Null(await db.Appointments.Where(a => a.AppointmentId == id).Select(a => a.LastLatitude).SingleAsync());
        Assert.False(await db.AppointmentEtas.AnyAsync(e => e.AppointmentId == id && e.Latitude != null));
        Assert.True(await db.AppointmentEtas.AnyAsync(e => e.AppointmentId == id));
        Assert.True(await db.GeofenceEvents.AnyAsync(e => e.AppointmentId == id && e.Latitude == null));
        Assert.False(await db.DriverOtps.AnyAsync());

        // The ops screen shows the run and the settings to look at.
        var admin = await As(DemoDataSeeder.TerminalAdminEmail);
        var status = await Read<OpsStatus>(await admin.GetAsync("/api/ops/status"));
        Assert.NotNull(status.LastCleanUp);
        Assert.Contains(status.Warnings, w => w.Contains("Tos:Adapter is Fake"));
    }

    [Fact]
    public async Task Sign_in_codes_cannot_be_guessed_in_parallel()
    {
        if (!Enabled) return;

        var code = await StartAsync("install-phone-0001");
        var wrong = code == "000000" ? "111111" : "000000";

        // 20 wrong guesses at the same moment: only 5 may count as tries, then the code is dead.
        var guesses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => VerifyAsync("install-phone-0001", wrong)));
        Assert.All(guesses, g => Assert.Equal(HttpStatusCode.Unauthorized, g.StatusCode));
        Assert.Equal(HttpStatusCode.Unauthorized, (await VerifyAsync("install-phone-0001", code)).StatusCode);

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TasDbContext>();
        Assert.Equal(5, await db.DriverOtps.Select(o => o.Attempts).SingleAsync());
    }

    [Fact]
    public async Task Password_guesses_in_parallel_still_lock_the_account()
    {
        if (!Enabled) return;

        var guesses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => LoginRaw(DemoDataSeeder.PlannerEmail, "wrong-password-1")));
        Assert.All(guesses, g => Assert.Contains(g.StatusCode, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Locked }));
        Assert.Equal(HttpStatusCode.Locked, (await LoginRaw(DemoDataSeeder.PlannerEmail, Password)).StatusCode);
    }

    [Fact]
    public async Task The_right_code_sent_twice_at_once_signs_in_once()
    {
        if (!Enabled) return;

        var code = await StartAsync("install-phone-0001");
        var both = await Task.WhenAll(VerifyAsync("install-phone-0001", code), VerifyAsync("install-phone-0001", code));
        Assert.Single(both, r => r.StatusCode == HttpStatusCode.OK);
    }

    [Fact]
    public void Test_switches_stop_a_real_server_from_starting()
    {
        if (!Enabled) return;

        // The test settings expose sign-in codes and password links; as a production server that must not start.
        using var production = Factory.WithWebHostBuilder(b => b.UseEnvironment("Production"));
        var error = Assert.ThrowsAny<Exception>(() => production.CreateClient());
        var messages = string.Join(" ", Messages(error));
        Assert.Contains("ExposeCodeForTesting", messages);
        Assert.Contains("ExposeTokenForTesting", messages);

        static IEnumerable<string> Messages(Exception? e)
        {
            for (; e is not null; e = e.InnerException)
                yield return e.Message;
        }
    }
}
