using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tas.Infrastructure;
using Tas.Infrastructure.Assistant;
using Tas.Infrastructure.Persistence;

namespace Tas.Api.Tests;

/// <summary>The AI assistant: guides and context in the prompt, read-only look-ups with the user's own permissions, limits.</summary>
public sealed class AssistantApiTests(TasApiFactory factory) : ApiTestBase(factory)
{
    private FakeAssistantModel Model => Factory.Assistant;
    private string Chat => $"{Terminal}/assistant";

    private static object Ask(string text, string lang = "en") => new { lang, messages = new[] { new { role = "user", text } } };

    private async Task<string> BookAsync()
    {
        var alNoor = await As(DemoDataSeeder.DispatcherEmail);
        var booked = await alNoor.PostAsJsonAsync(Appointments, ImportBooking());
        Assert.Equal(HttpStatusCode.Created, booked.StatusCode);
        return (await Read<Tas.Application.Booking.AppointmentDto>(booked)).AppointmentNo;
    }

    [Fact]
    public async Task Answers_from_the_guides_and_the_users_own_appointment()
    {
        if (!Enabled) return;
        Model.Reset();
        var number = await BookAsync();
        var alNoor = await As(DemoDataSeeder.DispatcherEmail);

        var info = await Read<AssistantInfo>(await alNoor.GetAsync("/api/assistant"));
        Assert.True(info.Enabled);
        Assert.True(info.LiveData);

        Model.Then(_ => FakeAssistantModel.Tool("call-1", "get_appointment", new { appointment_no = number }));
        Model.Then(request =>
        {
            var results = request["messages"]!.AsArray()[^1]!["content"]!.AsArray();
            var result = results[0]!.AsObject();
            Assert.Equal("call-1", (string?)result["tool_use_id"]);
            Assert.Null(result["is_error"]);
            Assert.Contains(number, (string?)result["content"]);
            Assert.Contains("IMP_PICKUP", (string?)result["content"]);
            return FakeAssistantModel.Text($"**{number}** is booked and clear to go. See [Appointments](#/appointments).");
        });

        var response = await alNoor.PostAsJsonAsync(Chat, Ask($"Is {number} ready to go?"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var answer = await Read<ChatAnswer>(response);
        Assert.Contains("clear to go", answer.Text);
        Assert.Equal(["get_appointment"], answer.LookedUp);
        Assert.Equal(59, answer.QuestionsLeft);

        // What the model was given: the guides (cached), the user's context, read-only tools for a company user.
        var first = Model.Requests[0];
        var system = first["system"]!.AsArray();
        Assert.Contains("Book a truck", (string?)system[0]!["text"]);
        Assert.NotNull(system[0]!["cache_control"]);
        Assert.Contains("CompanyAdmin, company Al Noor Transport", (string?)system[1]!["text"]);
        Assert.Equal("claude-sonnet-5-5", (string?)first["model"]);
        var tools = first["tools"]!.AsArray().Select(t => (string?)t!["name"]).ToList();
        Assert.Contains("get_free_hours", tools);
        Assert.DoesNotContain("terminal_now", tools); // terminal staff only

        // Only counts are kept, no text.
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TasDbContext>();
        var usage = await db.Database.SqlQuery<long>($"SELECT input_tokens AS \"Value\" FROM assistant_usage").SingleAsync();
        Assert.Equal(120 + 100 + 900, usage);
    }

    [Fact]
    public async Task Look_ups_never_show_another_companys_data()
    {
        if (!Enabled) return;
        Model.Reset();
        var number = await BookAsync(); // Al Noor's
        var gulf = await As(DemoDataSeeder.OtherDispatcherEmail);

        Model.Then(_ => FakeAssistantModel.Tool("call-1", "get_appointment", new { appointment_no = number }));
        Model.Then(request =>
        {
            var result = request["messages"]!.AsArray()[^1]!["content"]!.AsArray()[0]!.AsObject();
            Assert.True((bool?)result["is_error"]);
            Assert.Contains("not found", (string?)result["content"]);
            Assert.DoesNotContain("Al Noor", (string?)result["content"]);
            return FakeAssistantModel.Text("I can't find that appointment for your company.");
        });

        var answer = await Read<ChatAnswer>(await gulf.PostAsJsonAsync(Chat, Ask($"What is in {number}?")));
        Assert.StartsWith("I can't find", answer.Text);
    }

    [Fact]
    public async Task Terminal_staff_can_ask_for_todays_figures_and_free_hours()
    {
        if (!Enabled) return;
        Model.Reset();
        var planner = await As(DemoDataSeeder.PlannerEmail);

        Model.Then(request =>
        {
            Assert.Contains("terminal_now", request["tools"]!.AsArray().Select(t => (string?)t!["name"]));
            Assert.Contains("<technical-page id=\"integration\">", (string?)request["system"]![0]!["text"]);
            return FakeAssistantModel.Tool("a", "terminal_now", new { });
        });
        Model.Then(_ => FakeAssistantModel.Tool("b", "get_free_hours", new { date = Tomorrow.ToString("yyyy-MM-dd"), move_type = "IMP_PICKUP" }));
        Model.Then(request =>
        {
            var free = (string?)request["messages"]!.AsArray()[^1]!["content"]![0]!["content"];
            using var doc = JsonDocument.Parse(free!);
            Assert.True(doc.RootElement.GetProperty("hours").GetArrayLength() > 0);
            Assert.Equal("10:00", doc.RootElement.GetProperty("hours").EnumerateArray().First(h => h.GetProperty("from").GetString() == "10:00").GetProperty("from").GetString());
            return FakeAssistantModel.Text("Nobody is at the gate; tomorrow 10:00 has free places.");
        });

        var answer = await Read<ChatAnswer>(await planner.PostAsJsonAsync(Chat, Ask("كيف الوضع الآن وهل توجد أماكن غداً؟", "ar")));
        Assert.Equal(["terminal_now", "get_free_hours"], answer.LookedUp);
        Assert.Contains("Portal language: Arabic", (string?)Model.Requests[0]["system"]![1]!["text"]);
        Assert.Contains("حجز شاحنة", (string?)Model.Requests[0]["system"]![0]!["text"]); // the Arabic guides
    }

    [Fact]
    public async Task Limits_errors_and_bad_requests()
    {
        if (!Enabled) return;
        Model.Reset();
        var alNoor = await As(DemoDataSeeder.DispatcherEmail);

        // The last message must be the user's.
        var bad = await alNoor.PostAsJsonAsync(Chat, new { messages = new[] { new { role = "assistant", text = "Hello" } } });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        // The model is down: a friendly 503 and the question is not counted.
        Model.Then(_ => throw new AssistantUnavailableException("down"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await alNoor.PostAsJsonAsync(Chat, Ask("Hello"))).StatusCode);
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TasDbContext>();
            Assert.Equal(0, await db.Database.SqlQuery<int>($"SELECT coalesce(sum(questions), 0)::int AS \"Value\" FROM assistant_usage").SingleAsync());
            // Used up for today.
            var userId = await db.AppUsers.Where(u => u.Email == DemoDataSeeder.DispatcherEmail).Select(u => u.UserId).SingleAsync();
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            await db.Database.ExecuteSqlAsync($"INSERT INTO assistant_usage (user_id, day, questions) VALUES ({userId}, {today}, 60) ON CONFLICT (user_id, day) DO UPDATE SET questions = 60");
        }
        var limited = await alNoor.PostAsJsonAsync(Chat, Ask("One more?"));
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Contains("daily limit", await limited.Content.ReadAsStringAsync());

        // Not signed in: nothing.
        Assert.Equal(HttpStatusCode.Unauthorized, (await Factory.CreateClient().PostAsJsonAsync(Chat, Ask("Hi"))).StatusCode);
    }

    [Fact]
    public async Task Too_many_look_ups_end_with_an_answer()
    {
        if (!Enabled) return;
        Model.Reset();
        var alNoor = await As(DemoDataSeeder.DispatcherEmail);
        for (var i = 0; i < 5; i++)
            Model.Then(_ => FakeAssistantModel.Tool($"c{i}", "list_move_types", new { }));
        Model.Then(request =>
        {
            Assert.Equal("none", (string?)request["tool_choice"]!["type"]);
            return FakeAssistantModel.Text("Here are the move types.");
        });
        var answer = await Read<ChatAnswer>(await alNoor.PostAsJsonAsync(Chat, Ask("Move types?")));
        Assert.Equal(5, answer.LookedUp.Count);
        Assert.Equal("Here are the move types.", answer.Text);
    }
}
