using System.Net;
using System.Net.Http.Json;
using Yardsyncs.Application.Auth;
using Yardsyncs.Application.Booking;
using Microsoft.Extensions.DependencyInjection;
using Yardsyncs.Infrastructure;

namespace Yardsyncs.Api.Tests;

/// <summary>A trucking company's own system books with an API key instead of a person's sign-in.</summary>
public sealed class CompanyApiKeyApiTests(YardsyncsApiFactory factory) : ApiTestBase(factory)
{
    private HttpClient WithKey(string key)
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", key);
        return client;
    }

    [Fact]
    public async Task The_companys_tms_books_with_a_key_and_a_read_only_key_only_looks()
    {
        if (!Enabled) return;
        var dispatcher = await As(DemoDataSeeder.DispatcherEmail); // Al Noor's company admin
        var keys = $"/api/companies/{Demo.CompanyId}/api-keys";

        var created = await dispatcher.PostAsJsonAsync(keys, new { name = "Our TMS", scope = "Book" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var book = (await Read<AdminResponse<CompanyApiKeyIssued>>(created)).Data;
        Assert.StartsWith(CompanyApiKeys.Prefix, book.Key);
        var look = (await Read<AdminResponse<CompanyApiKeyIssued>>(await dispatcher.PostAsJsonAsync(keys, new { name = "Reports", scope = "Read" }))).Data;
        Assert.Equal(HttpStatusCode.Conflict, (await dispatcher.PostAsJsonAsync(keys, new { name = "Our TMS" })).StatusCode);

        // Books as the company: no company id needed, as for its own users.
        var tms = WithKey(book.Key);
        var booked = await tms.PostAsJsonAsync(Appointments, new { slotId = SlotId, truckId = Demo.TruckIds[0], items = new[] { new { category = "IMP_PICKUP", containerNo = "MSCU1234566" } } });
        Assert.Equal(HttpStatusCode.Created, booked.StatusCode);
        var number = (await Read<AppointmentDto>(booked)).AppointmentNo;

        // The read-only key sees it but can't change anything.
        var reports = WithKey(look.Key);
        Assert.Equal(HttpStatusCode.OK, (await reports.GetAsync($"{Appointments}/{number}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reports.PostAsJsonAsync($"{Appointments}/{number}/cancel", new { reason = "test" })).StatusCode);

        // Keys don't manage keys; other companies don't see them; planners don't make them.
        Assert.Equal(HttpStatusCode.Forbidden, (await tms.GetAsync(keys)).StatusCode);
        var other = await As(DemoDataSeeder.OtherDispatcherEmail);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync(keys)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"{Appointments}/{number}")).StatusCode);
        var planner = await As(DemoDataSeeder.PlannerEmail);
        Assert.Equal(HttpStatusCode.Forbidden, (await planner.PostAsJsonAsync(keys, new { name = "Planner's" })).StatusCode);

        // The admin sees when it was used; revoking stops it at once.
        var listed = await Read<List<CompanyApiKeyView>>(await dispatcher.GetAsync(keys));
        Assert.NotNull(listed.Single(k => k.Name == "Our TMS").LastUsedAt);
        Assert.Equal(HttpStatusCode.OK, (await dispatcher.DeleteAsync($"{keys}/{book.ApiKey.KeyId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await tms.GetAsync($"{Appointments}/{number}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await reports.GetAsync($"{Appointments}/{number}")).StatusCode);
    }

    [Fact]
    public async Task The_account_behind_a_key_is_not_a_person_to_manage()
    {
        if (!Enabled) return;
        var dispatcher = await As(DemoDataSeeder.DispatcherEmail);
        var issued = (await Read<AdminResponse<CompanyApiKeyIssued>>(await dispatcher.PostAsJsonAsync($"/api/companies/{Demo.CompanyId}/api-keys", new { name = "Our TMS" }))).Data;
        int accountId;
        using (var scope = Factory.Services.CreateScope())
            accountId = scope.ServiceProvider.GetRequiredService<Yardsyncs.Infrastructure.Persistence.YardsyncsDbContext>()
                .CompanyApiKeys.Single(k => k.KeyId == issued.ApiKey.KeyId).UserId;

        // Not in the user list; can't be given a role, a password or a new activation.
        var admin = await As(DemoDataSeeder.TerminalAdminEmail);
        var users = await Read<List<System.Text.Json.JsonElement>>(await admin.GetAsync($"/api/users?companyId={Demo.CompanyId}"));
        Assert.DoesNotContain(users, u => u.GetProperty("userId").GetInt32() == accountId);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PutAsJsonAsync($"/api/users/{accountId}",
            new { fullName = "API: Our TMS", mobile = (string?)null, preferredLanguage = "en", role = "CompanyAdmin" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsJsonAsync($"/api/users/{accountId}/reset-password", new { newPassword = "Yard-Takeover-2026" })).StatusCode);
    }
}
