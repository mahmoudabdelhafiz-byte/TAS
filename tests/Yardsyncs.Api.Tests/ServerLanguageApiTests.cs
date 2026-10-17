using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Yardsyncs.Infrastructure;

namespace Yardsyncs.Api.Tests;

/// <summary>The server's own sentences (check results, errors) in the language the portal asks for.</summary>
public sealed class ServerLanguageApiTests(YardsyncsApiFactory factory) : ApiTestBase(factory)
{
    [Fact]
    public async Task An_arabic_portal_gets_check_results_and_errors_in_arabic()
    {
        if (!Enabled) return;

        var client = await As(DemoDataSeeder.DispatcherEmail);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ar-SA");

        // A container on customs hold can't be booked: the reason comes back in Arabic, the container number as it is.
        var refused = await client.PostAsJsonAsync(Appointments, ImportBooking(container: "MSCU7654329"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        var problem = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("حجز جمركي على MSCU7654329", problem.GetProperty("detail").GetString());
        Assert.Contains(problem.GetProperty("checks").EnumerateArray(),
            c => c.GetProperty("message").GetString() is { } m && m.Contains("حجز جمركي"));

        // Wrong password, in Arabic; English when the portal is in English.
        var anonymous = Factory.CreateClient();
        anonymous.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ar");
        var wrong = await anonymous.PostAsJsonAsync("/api/auth/login", new { email = "nobody@example.com", password = "not the password" });
        Assert.Equal("البريد الإلكتروني أو كلمة المرور غير صحيحة.", (await wrong.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString());

        var english = await Factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email = "nobody@example.com", password = "not the password" });
        Assert.Equal("Wrong email or password.", (await english.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task A_booked_appointment_reads_its_ready_to_go_reason_in_arabic()
    {
        if (!Enabled) return;

        var client = await As(DemoDataSeeder.DispatcherEmail);
        var created = await client.PostAsJsonAsync(Appointments, ImportBooking());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var number = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("appointmentNo").GetString();
        var english = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("trafficReason").GetString();

        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ar");
        var arabic = (await client.GetFromJsonAsync<JsonElement>($"{Appointments}/{number}")).GetProperty("trafficReason").GetString();
        Assert.NotEqual(english, arabic);
        Assert.Matches(@"\p{IsArabic}", arabic!);
    }
}
