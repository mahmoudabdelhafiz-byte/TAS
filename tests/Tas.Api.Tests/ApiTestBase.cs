using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tas.Infrastructure;
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
        builder.UseSetting("Revalidation:Enabled", "false");
        builder.UseSetting("Notifications:Enabled", "false");
        builder.UseSetting("Integration:Enabled", "false");
        builder.UseSetting("Privacy:CleanUpEnabled", "false"); // run by the test
        builder.UseSetting("Tracking:Enabled", "false"); // followed containers are checked by the test // events stay QUEUED; delivery is tested in Tas.Infrastructure.Tests
        builder.UseSetting("Notifications:PushProvider", "Fcm"); // push first for drivers with the app (not sent: the worker is off)
        builder.UseSetting("DriverLogin:ExposeCodeForTesting", "true");
        builder.UseSetting("PasswordLinks:ExposeTokenForTesting", "true");
        builder.UseSetting("FakeTos:CustomsHold:0", "MSCU7654329");
        builder.UseSetting("FakeTos:PaymentWarning:0", "CMAU5100028");
        builder.UseSetting("FakeTos:YardPositions:MSCU1234566", "B2.14.03.2");
        builder.UseSetting("FakeTos:DefaultBlocks:0", "A1");
        builder.UseSetting("FakeTos:DefaultBlocks:1", "B2");
        builder.UseSetting("FakeTos:ReceivalBlocks:EXP_DROP", "E1");
        builder.UseSetting("FakeTos:ReceivalBlocks:MTY_RETURN", "M1");
        builder.UseSetting("FakeTos:BillsOfLading:MEDUJD260001:0", "MSCU1234566:22G1");
        builder.UseSetting("FakeTos:BillsOfLading:MEDUJD260001:1", "MSCU5523417:22G1");
        builder.UseSetting("FakeTos:BillsOfLading:MEDUJD260001:2", "MEDU2601003:45G1");
        builder.UseSetting("FakeTos:BillsOfLading:MEDUJD260001:3", "MSCU7654329:22G1");
        builder.UseSetting("FakeTos:BillsOfLading:MEDUJD260001:4", "CMAU5100028:42G1");
    }
}

/// <summary>
/// Fresh demo data before every test, plus helpers to sign in as the demo users.
/// Tests return early (no-op) when TAS_API_TEST_DB is not set, except in CI where it is required.
/// </summary>
public abstract class ApiTestBase(TasApiFactory factory) : IClassFixture<TasApiFactory>, IAsyncLifetime
{
    protected static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    protected const string Password = DemoDataSeeder.DemoPassword;
    protected static readonly TimeZoneInfo Riyadh = TimeZoneInfo.FindSystemTimeZoneById("Asia/Riyadh");

    protected TasApiFactory Factory { get; } = factory;
    protected static bool Enabled => TasApiFactory.ConnectionString is not null;
    protected DemoSeedResult Demo { get; private set; } = null!;

    /// <summary>Tomorrow (Riyadh) — every day of the demo template has a 10:00 slot.</summary>
    protected DateOnly Tomorrow { get; private set; }

    /// <summary>The slot tomorrow 10:00–11:00 Riyadh time.</summary>
    protected long SlotId { get; private set; }

    public async Task InitializeAsync()
    {
        if (!Enabled)
        {
            if (Environment.GetEnvironmentVariable("CI") == "true")
                throw new InvalidOperationException("TAS_API_TEST_DB is not set in CI.");
            return;
        }

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TasDbContext>();
        await db.Database.ExecuteSqlRawAsync("TRUNCATE terminal, company, app_user, driver_otp RESTART IDENTITY CASCADE");
        Demo = await scope.ServiceProvider.GetRequiredService<DemoDataSeeder>().SeedAsync();

        Tomorrow = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Riyadh).DateTime).AddDays(1);
        var start = At(Tomorrow, 10).ToUniversalTime();
        SlotId = await db.Slots.Where(s => s.TerminalId == Demo.TerminalId && s.StartAt == start).Select(s => s.SlotId).SingleAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>A local Riyadh time as an offset value (+03:00).</summary>
    protected static DateTimeOffset At(DateOnly date, int hour) => new(date.ToDateTime(new TimeOnly(hour, 0)), TimeSpan.FromHours(3));

    protected string Terminal => $"/api/terminals/{Demo.TerminalId}";
    protected string Appointments => $"{Terminal}/appointments";

    protected async Task<HttpResponseMessage> LoginRaw(string email, string password) =>
        await Factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email, password });

    protected async Task<TokenResponse> Login(string email, string password = Password)
    {
        var response = await LoginRaw(email, password);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<TokenResponse>(Json))!;
    }

    protected async Task<HttpClient> As(string email, string password = Password)
    {
        var tokens = await Login(email, password);
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return client;
    }

    protected object ImportBooking(int? companyId = null, string container = "MSCU1234566", long? slotId = null) => new
    {
        slotId = slotId ?? SlotId,
        companyId,
        items = new[] { new { category = "IMP_PICKUP", containerNo = container } },
    };

    protected static async Task<T> Read<T>(HttpResponseMessage response) => (await response.Content.ReadFromJsonAsync<T>(Json))!;
}
