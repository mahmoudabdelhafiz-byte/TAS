using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yardsyncs.Infrastructure;
using Yardsyncs.Infrastructure.Persistence;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Yardsyncs.Api.Tests;

/// <summary>The real API in memory against PostgreSQL (TAS_API_TEST_DB), with the demo terminal seeded.</summary>
public sealed class YardsyncsApiFactory : WebApplicationFactory<Program>
{
    public static readonly string? ConnectionString = Environment.GetEnvironmentVariable("TAS_API_TEST_DB");

    /// <summary>Stands in for the AI model: each test scripts its answers.</summary>
    public FakeAssistantModel Assistant { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Yardsyncs", ConnectionString);
        builder.UseSetting("Jwt:SigningKey", "test-only-signing-key-0123456789-abcdefghijkl");
        builder.UseSetting("RateLimit:AuthPerMinute", "1000");
        builder.UseSetting("SlotGeneration:Enabled", "false");
        builder.UseSetting("NoShow:Enabled", "false");
        builder.UseSetting("Revalidation:Enabled", "false");
        builder.UseSetting("Notifications:Enabled", "false");
        builder.UseSetting("Integration:Enabled", "false");
        builder.UseSetting("Privacy:CleanUpEnabled", "false"); // run by the test
        builder.UseSetting("Assistant:ApiKey", "test-key"); // the model is FakeAssistantModel below
        builder.UseSetting("Assistant:PerMinute", "1000");
        builder.ConfigureTestServices(services => services.AddSingleton<Yardsyncs.Infrastructure.Assistant.IAssistantModel>(Assistant));
        builder.UseSetting("Languages:Path", Path.Combine(AppContext.BaseDirectory, "languages")); // fr.json
        builder.UseSetting("Tracking:Enabled", "false"); // followed containers are checked by the test // events stay QUEUED; delivery is tested in Yardsyncs.Infrastructure.Tests
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
public abstract class ApiTestBase(YardsyncsApiFactory factory) : IClassFixture<YardsyncsApiFactory>, IAsyncLifetime
{
    protected static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    protected const string Password = DemoDataSeeder.DemoPassword;
    protected static readonly TimeZoneInfo Riyadh = TimeZoneInfo.FindSystemTimeZoneById("Asia/Riyadh");

    protected YardsyncsApiFactory Factory { get; } = factory;
    protected static bool Enabled => YardsyncsApiFactory.ConnectionString is not null;
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
        var db = scope.ServiceProvider.GetRequiredService<YardsyncsDbContext>();
        await db.Database.ExecuteSqlRawAsync("TRUNCATE terminal_operator, terminal, company, app_user, driver_otp RESTART IDENTITY CASCADE");
        Demo = await scope.ServiceProvider.GetRequiredService<DemoDataSeeder>().SeedAsync();
        Yardsyncs.Infrastructure.Brands.BrandingService.Forget(); // the data was just replaced

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

/// <summary>A scripted AI model: answers in order, and keeps the requests it got.</summary>
public sealed class FakeAssistantModel : Yardsyncs.Infrastructure.Assistant.IAssistantModel
{
    private readonly Queue<Func<System.Text.Json.Nodes.JsonObject, System.Text.Json.Nodes.JsonObject>> _answers = new();
    public List<System.Text.Json.Nodes.JsonObject> Requests { get; } = [];

    public void Reset()
    {
        _answers.Clear();
        Requests.Clear();
    }

    public void Then(Func<System.Text.Json.Nodes.JsonObject, System.Text.Json.Nodes.JsonObject> answer) => _answers.Enqueue(answer);

    public Task<System.Text.Json.Nodes.JsonObject> SendAsync(System.Text.Json.Nodes.JsonObject request, CancellationToken ct)
    {
        Requests.Add(request);
        if (_answers.Count == 0)
            throw new InvalidOperationException("The test did not script an answer.");
        return Task.FromResult(_answers.Dequeue()(request));
    }

    public static System.Text.Json.Nodes.JsonObject Text(string text) => System.Text.Json.Nodes.JsonNode.Parse(
        System.Text.Json.JsonSerializer.Serialize(new
        {
            stop_reason = "end_turn",
            content = new object[] { new { type = "text", text } },
            usage = new { input_tokens = 100, output_tokens = 20, cache_read_input_tokens = 900 },
        }))!.AsObject();

    public static System.Text.Json.Nodes.JsonObject Tool(string id, string name, object input) => System.Text.Json.Nodes.JsonNode.Parse(
        System.Text.Json.JsonSerializer.Serialize(new
        {
            stop_reason = "tool_use",
            content = new object[] { new { type = "text", text = "Let me check." }, new { type = "tool_use", id, name, input } },
            usage = new { input_tokens = 120, output_tokens = 30 },
        }))!.AsObject();
}
