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

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Tas.Api.Tests;

/// <summary>The real API in memory against PostgreSQL (TAS_API_TEST_DB), with the demo terminal seeded.</summary>
public sealed class TasApiFactory : WebApplicationFactory<Program>
{
    public static readonly string? ConnectionString = Environment.GetEnvironmentVariable("TAS_API_TEST_DB");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Tas", ConnectionString);
        builder.UseSetting("Jwt:SigningKey", "test-only-signing-key-0123456789-abcdefghijkl");
        builder.UseSetting("RateLimit:AuthPerMinute", "1000");
        builder.UseSetting("SlotGeneration:Enabled", "false");
        builder.UseSetting("NoShow:Enabled", "false");
        builder.UseSetting("FakeTos:CustomsHold:0", "MSCU7654329");
    }
}

public sealed class AuthApiTests(TasApiFactory factory) : IClassFixture<TasApiFactory>, IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private const string Password = DemoDataSeeder.DemoPassword;

    private DemoSeedResult _demo = null!;
    private long _slotId;

    public async Task InitializeAsync()
    {
        if (TasApiFactory.ConnectionString is null)
        {
            if (Environment.GetEnvironmentVariable("CI") == "true")
                throw new InvalidOperationException("TAS_API_TEST_DB is not set in CI.");
            return;
        }

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TasDbContext>();
        await db.Database.ExecuteSqlRawAsync("TRUNCATE terminal, company, app_user RESTART IDENTITY CASCADE");
        _demo = await scope.ServiceProvider.GetRequiredService<DemoDataSeeder>().SeedAsync();

        // Tomorrow 10:00 Riyadh time — always a bookable slot in the demo template.
        var riyadh = TimeZoneInfo.FindSystemTimeZoneById("Asia/Riyadh");
        var tomorrow = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, riyadh).DateTime).AddDays(1);
        var start = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(tomorrow.ToDateTime(new TimeOnly(10, 0)), riyadh), TimeSpan.Zero);
        _slotId = await db.Slots.Where(s => s.TerminalId == _demo.TerminalId && s.StartAt == start).Select(s => s.SlotId).SingleAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ------------------------------------------------------------ helpers

    private async Task<HttpResponseMessage> LoginRaw(string email, string password) =>
        await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email, password });

    private async Task<TokenResponse> Login(string email, string password = Password)
    {
        var response = await LoginRaw(email, password);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<TokenResponse>(Json))!;
    }

    private async Task<HttpClient> As(string email, string password = Password)
    {
        var tokens = await Login(email, password);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return client;
    }

    private string Appointments => $"/api/terminals/{_demo.TerminalId}/appointments";

    private object ImportBooking(int? companyId = null, string container = "MSCU1234566") => new
    {
        slotId = _slotId,
        companyId,
        items = new[] { new { category = "IMP_PICKUP", containerNo = container } },
    };

    private static async Task<T> Read<T>(HttpResponseMessage response) => (await response.Content.ReadFromJsonAsync<T>(Json))!;

    // ------------------------------------------------------------ tests

    [Fact]
    public async Task Requests_without_a_valid_token_are_rejected()
    {
        if (TasApiFactory.ConnectionString is null) return;

        var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"{Appointments}?date=2026-10-20")).StatusCode);

        var tokens = await Login(DemoDataSeeder.DispatcherEmail);
        var tampered = factory.CreateClient();
        tampered.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken[..^4] + "AAAA");
        Assert.Equal(HttpStatusCode.Unauthorized, (await tampered.GetAsync("/api/auth/me")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/health")).StatusCode);
    }

    [Fact]
    public async Task Login_returns_tokens_and_profile()
    {
        if (TasApiFactory.ConnectionString is null) return;

        var tokens = await Login("  DISPATCHER@alnoor.example ");
        Assert.Equal(UserRole.CompanyAdmin, tokens.User.Role);
        Assert.Equal("Al Noor Transport", tokens.User.CompanyName);
        Assert.True(tokens.AccessTokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(25));

        var me = await Read<UserProfile>(await (await As(DemoDataSeeder.PlannerEmail)).GetAsync("/api/auth/me"));
        Assert.Equal(UserRole.Planner, me.Role);
        Assert.Equal(_demo.TerminalId, me.TerminalId);
    }

    [Fact]
    public async Task Five_wrong_passwords_lock_the_account()
    {
        if (TasApiFactory.ConnectionString is null) return;

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
        if (TasApiFactory.ConnectionString is null) return;

        var alNoor = await As(DemoDataSeeder.DispatcherEmail);
        var gulf = await As(DemoDataSeeder.OtherDispatcherEmail);

        // Booked without companyId: it comes from the account.
        var created = await alNoor.PostAsJsonAsync(Appointments, ImportBooking());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var appointment = await Read<AppointmentDto>(created);
        Assert.Equal(_demo.CompanyId, appointment.CompanyId);

        Assert.Equal(HttpStatusCode.OK, (await alNoor.GetAsync($"{Appointments}/{appointment.AppointmentNo}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await gulf.GetAsync($"{Appointments}/{appointment.AppointmentNo}")).StatusCode);

        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(appointment.SlotStart, TimeZoneInfo.FindSystemTimeZoneById("Asia/Riyadh")).DateTime);
        var gulfList = await Read<List<AppointmentDto>>(await gulf.GetAsync($"{Appointments}?date={date:yyyy-MM-dd}"));
        Assert.Empty(gulfList);
        Assert.Equal(HttpStatusCode.Forbidden, (await gulf.GetAsync($"{Appointments}?date={date:yyyy-MM-dd}&companyId={_demo.CompanyId}")).StatusCode);

        // Gulf Haulage can neither book for Al Noor nor cancel its appointment.
        Assert.Equal(HttpStatusCode.Forbidden, (await gulf.PostAsJsonAsync(Appointments, ImportBooking(_demo.CompanyId, "MAEU4001110"))).StatusCode);
        var cancel = await gulf.PostAsJsonAsync($"{Appointments}/{appointment.AppointmentNo}/cancel", new { reason = "not mine" });
        Assert.Equal(HttpStatusCode.BadRequest, cancel.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await alNoor.PostAsJsonAsync($"{Appointments}/{appointment.AppointmentNo}/cancel", new { reason = "mine" })).StatusCode);
    }

    [Fact]
    public async Task Terminal_staff_book_on_behalf_and_manage_capacity()
    {
        if (TasApiFactory.ConnectionString is null) return;

        var planner = await As(DemoDataSeeder.PlannerEmail);
        var dispatcher = await As(DemoDataSeeder.DispatcherEmail);

        Assert.Equal(HttpStatusCode.BadRequest, (await planner.PostAsJsonAsync(Appointments, ImportBooking())).StatusCode); // must name the company
        var onBehalf = await planner.PostAsJsonAsync(Appointments, ImportBooking(_demo.CompanyId));
        Assert.Equal(HttpStatusCode.Created, onBehalf.StatusCode);

        // Staff see every company's appointments.
        var appointment = await Read<AppointmentDto>(onBehalf);
        Assert.Equal(HttpStatusCode.OK, (await planner.GetAsync($"{Appointments}/{appointment.AppointmentNo}")).StatusCode);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var generate = $"/api/terminals/{_demo.TerminalId}/slots/generate?from={today:yyyy-MM-dd}&to={today.AddDays(1):yyyy-MM-dd}";
        Assert.Equal(HttpStatusCode.Forbidden, (await dispatcher.PostAsync(generate, null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await planner.PostAsync(generate, null)).StatusCode);
    }

    [Fact]
    public async Task Staff_of_another_terminal_have_no_access()
    {
        if (TasApiFactory.ConnectionString is null) return;

        using (var scope = factory.Services.CreateScope())
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
            (await outsider.GetAsync($"/api/terminals/{_demo.TerminalId}/availability?date=2026-10-20&category=IMP_PICKUP")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.PostAsJsonAsync(Appointments, ImportBooking(_demo.CompanyId))).StatusCode);
    }

    [Fact]
    public async Task Refresh_tokens_rotate_and_reuse_signs_out_everywhere()
    {
        if (TasApiFactory.ConnectionString is null) return;

        var client = factory.CreateClient();
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
        if (TasApiFactory.ConnectionString is null) return;

        var client = factory.CreateClient();
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
        if (TasApiFactory.ConnectionString is null) return;

        var companyAdmin = await As(DemoDataSeeder.DispatcherEmail);
        var terminalAdmin = await As(DemoDataSeeder.TerminalAdminEmail);

        object NewUser(string email, string role, int? companyId = null, int? terminalId = null, string password = "Booking-Desk-77") =>
            new { email, fullName = "New user", role, companyId, terminalId, password, preferredLanguage = "ar" };

        var created = await companyAdmin.PostAsJsonAsync("/api/users", NewUser("clerk2@alnoor.example", "CompanyUser", _demo.CompanyId));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await LoginRaw("clerk2@alnoor.example", "Booking-Desk-77")).StatusCode);

        Assert.Equal(HttpStatusCode.Conflict, (await companyAdmin.PostAsJsonAsync("/api/users", NewUser("CLERK2@alnoor.example", "CompanyUser", _demo.CompanyId))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await companyAdmin.PostAsJsonAsync("/api/users", NewUser("clerk3@alnoor.example", "CompanyUser", _demo.CompanyId, password: "weak"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await companyAdmin.PostAsJsonAsync("/api/users", NewUser("x@alnoor.example", "CompanyUser", _demo.OtherCompanyId))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await companyAdmin.PostAsJsonAsync("/api/users", NewUser("p@alnoor.example", "Planner", terminalId: _demo.TerminalId))).StatusCode);

        Assert.Equal(HttpStatusCode.Created, (await terminalAdmin.PostAsJsonAsync("/api/users", NewUser("gate1@demo-terminal.example", "GateClerk", terminalId: _demo.TerminalId))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await terminalAdmin.PostAsJsonAsync("/api/users", NewUser("root@demo-terminal.example", "SysAdmin"))).StatusCode);
    }
}
