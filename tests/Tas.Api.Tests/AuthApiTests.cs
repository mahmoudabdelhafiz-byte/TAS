using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tas.Api;
using Tas.Application.Booking;
using Tas.Domain;
using Tas.Domain.Parties;
using Tas.Infrastructure;
using Tas.Infrastructure.Auth;
using Tas.Infrastructure.Persistence;

namespace Tas.Api.Tests;

public sealed class AuthApiTests(TasApiFactory factory) : ApiTestBase(factory)
{
    // ------------------------------------------------------------ tests

    [Fact]
    public async Task Requests_without_a_valid_token_are_rejected()
    {
        if (!Enabled) return;

        var anonymous = Factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"{Appointments}?date=2026-10-20")).StatusCode);

        var tokens = await Login(DemoDataSeeder.DispatcherEmail);
        var tampered = Factory.CreateClient();
        tampered.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken[..^4] + "AAAA");
        Assert.Equal(HttpStatusCode.Unauthorized, (await tampered.GetAsync("/api/auth/me")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/health")).StatusCode);
    }

    [Fact]
    public async Task Login_returns_tokens_and_profile()
    {
        if (!Enabled) return;

        var tokens = await Login("  DISPATCHER@alnoor.example ");
        Assert.Equal(UserRole.CompanyAdmin, tokens.User.Role);
        Assert.Equal("Al Noor Transport", tokens.User.CompanyName);
        Assert.True(tokens.AccessTokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(25));

        var me = await Read<UserProfile>(await (await As(DemoDataSeeder.PlannerEmail)).GetAsync("/api/auth/me"));
        Assert.Equal(UserRole.Planner, me.Role);
        Assert.Equal(Demo.TerminalId, me.TerminalId);
    }

    [Fact]
    public async Task Five_wrong_passwords_lock_the_account()
    {
        if (!Enabled) return;

        for (var i = 0; i < AuthService.MaxFailedLogins; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await LoginRaw(DemoDataSeeder.DispatcherEmail, "wrong-password-1")).StatusCode);

        // Even the right password is refused while locked.
        Assert.Equal(HttpStatusCode.Locked, (await LoginRaw(DemoDataSeeder.DispatcherEmail, Password)).StatusCode);

        // Unknown emails look exactly like wrong passwords.
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginRaw("nobody@nowhere.example", Password)).StatusCode);
    }

    [Fact]
    public async Task Companies_only_see_their_own_appointments()
    {
        if (!Enabled) return;

        var alNoor = await As(DemoDataSeeder.DispatcherEmail);
        var gulf = await As(DemoDataSeeder.OtherDispatcherEmail);

        // Booked without companyId: it comes from the account.
        var created = await alNoor.PostAsJsonAsync(Appointments, ImportBooking());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var appointment = await Read<AppointmentDto>(created);
        Assert.Equal(Demo.CompanyId, appointment.CompanyId);

        Assert.Equal(HttpStatusCode.OK, (await alNoor.GetAsync($"{Appointments}/{appointment.AppointmentNo}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await gulf.GetAsync($"{Appointments}/{appointment.AppointmentNo}")).StatusCode);

        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(appointment.SlotStart, TimeZoneInfo.FindSystemTimeZoneById("Asia/Riyadh")).DateTime);
        var gulfList = await Read<List<AppointmentDto>>(await gulf.GetAsync($"{Appointments}?date={date:yyyy-MM-dd}"));
        Assert.Empty(gulfList);
        Assert.Equal(HttpStatusCode.Forbidden, (await gulf.GetAsync($"{Appointments}?date={date:yyyy-MM-dd}&companyId={Demo.CompanyId}")).StatusCode);

        // Gulf Haulage can neither book for Al Noor nor cancel its appointment.
        Assert.Equal(HttpStatusCode.Forbidden, (await gulf.PostAsJsonAsync(Appointments, ImportBooking(Demo.CompanyId, "MAEU4001110"))).StatusCode);
        var cancel = await gulf.PostAsJsonAsync($"{Appointments}/{appointment.AppointmentNo}/cancel", new { reason = "not mine" });
        Assert.Equal(HttpStatusCode.BadRequest, cancel.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await alNoor.PostAsJsonAsync($"{Appointments}/{appointment.AppointmentNo}/cancel", new { reason = "mine" })).StatusCode);
    }

    [Fact]
    public async Task Terminal_staff_book_on_behalf_and_manage_capacity()
    {
        if (!Enabled) return;

        var planner = await As(DemoDataSeeder.PlannerEmail);
        var dispatcher = await As(DemoDataSeeder.DispatcherEmail);

        Assert.Equal(HttpStatusCode.BadRequest, (await planner.PostAsJsonAsync(Appointments, ImportBooking())).StatusCode); // must name the company
        var onBehalf = await planner.PostAsJsonAsync(Appointments, ImportBooking(Demo.CompanyId));
        Assert.Equal(HttpStatusCode.Created, onBehalf.StatusCode);

        // Staff see every company's appointments.
        var appointment = await Read<AppointmentDto>(onBehalf);
        Assert.Equal(HttpStatusCode.OK, (await planner.GetAsync($"{Appointments}/{appointment.AppointmentNo}")).StatusCode);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var generate = $"/api/terminals/{Demo.TerminalId}/slots/generate?from={today:yyyy-MM-dd}&to={today.AddDays(1):yyyy-MM-dd}";
        Assert.Equal(HttpStatusCode.Forbidden, (await dispatcher.PostAsync(generate, null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await planner.PostAsync(generate, null)).StatusCode);
    }

    [Fact]
    public async Task Staff_of_another_terminal_have_no_access()
    {
        if (!Enabled) return;

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TasDbContext>();
            var other = new Terminal { Code = "OTHER", Name = "Other terminal", TerminalType = TerminalType.Container };
            db.Terminals.Add(other);
            await db.SaveChangesAsync();
            var clerk = new AppUser { TerminalId = other.TerminalId, Email = "clerk@other.example", FullName = "Other clerk", Role = UserRole.GateClerk };
            clerk.PasswordHash = scope.ServiceProvider.GetRequiredService<PasswordService>().Hash(clerk, Password);
            db.AppUsers.Add(clerk);
            await db.SaveChangesAsync();
        }

        var outsider = await As("clerk@other.example");
        Assert.Equal(HttpStatusCode.Forbidden,
            (await outsider.GetAsync($"/api/terminals/{Demo.TerminalId}/availability?date=2026-10-20&category=IMP_PICKUP")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.PostAsJsonAsync(Appointments, ImportBooking(Demo.CompanyId))).StatusCode);
    }

    [Fact]
    public async Task Refresh_tokens_rotate_and_reuse_signs_out_everywhere()
    {
        if (!Enabled) return;

        var client = Factory.CreateClient();
        var first = await Login(DemoDataSeeder.DispatcherEmail);

        var refreshed = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = first.RefreshToken });
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        var second = await Read<TokenResponse>(refreshed);
        Assert.NotEqual(first.RefreshToken, second.RefreshToken);

        // Replaying the old token (as a thief would) is refused and kills the new one too.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = first.RefreshToken })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = second.RefreshToken })).StatusCode);
    }

    [Fact]
    public async Task Logout_and_password_change_end_sessions()
    {
        if (!Enabled) return;

        var client = Factory.CreateClient();
        var session = await Login(DemoDataSeeder.DispatcherEmail);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/auth/logout", new { refreshToken = session.RefreshToken })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = session.RefreshToken })).StatusCode);

        var other = await Login(DemoDataSeeder.DispatcherEmail);
        var user = await As(DemoDataSeeder.DispatcherEmail);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await user.PostAsJsonAsync("/api/auth/change-password", new { currentPassword = Password, newPassword = "short" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await user.PostAsJsonAsync("/api/auth/change-password", new { currentPassword = Password, newPassword = "Harbour-Crane-42" })).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = other.RefreshToken })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginRaw(DemoDataSeeder.DispatcherEmail, Password)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await LoginRaw(DemoDataSeeder.DispatcherEmail, "Harbour-Crane-42")).StatusCode);
    }

    [Fact]
    public async Task Admins_create_users_within_their_scope()
    {
        if (!Enabled) return;

        var companyAdmin = await As(DemoDataSeeder.DispatcherEmail);
        var terminalAdmin = await As(DemoDataSeeder.TerminalAdminEmail);

        object NewUser(string email, string role, int? companyId = null, int? terminalId = null, string password = "Booking-Desk-77") =>
            new { email, fullName = "New user", role, companyId, terminalId, password, preferredLanguage = "ar" };

        var created = await companyAdmin.PostAsJsonAsync("/api/users", NewUser("clerk2@alnoor.example", "CompanyUser", Demo.CompanyId));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await LoginRaw("clerk2@alnoor.example", "Booking-Desk-77")).StatusCode);

        Assert.Equal(HttpStatusCode.Conflict, (await companyAdmin.PostAsJsonAsync("/api/users", NewUser("CLERK2@alnoor.example", "CompanyUser", Demo.CompanyId))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await companyAdmin.PostAsJsonAsync("/api/users", NewUser("clerk3@alnoor.example", "CompanyUser", Demo.CompanyId, password: "weak"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await companyAdmin.PostAsJsonAsync("/api/users", NewUser("x@alnoor.example", "CompanyUser", Demo.OtherCompanyId))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await companyAdmin.PostAsJsonAsync("/api/users", NewUser("p@alnoor.example", "Planner", terminalId: Demo.TerminalId))).StatusCode);

        Assert.Equal(HttpStatusCode.Created, (await terminalAdmin.PostAsJsonAsync("/api/users", NewUser("gate1@demo-terminal.example", "GateClerk", terminalId: Demo.TerminalId))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await terminalAdmin.PostAsJsonAsync("/api/users", NewUser("root@demo-terminal.example", "SysAdmin"))).StatusCode);
    }
}
