using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Yardsyncs.Application.Notifications;
using Yardsyncs.Domain;
using Yardsyncs.Infrastructure;
using Yardsyncs.Infrastructure.Notifications;
using Yardsyncs.Infrastructure.Persistence;
using Yardsyncs.Infrastructure.Reports;

namespace Yardsyncs.Api.Tests;

/// <summary>Reports by email: who gets them, and the daily and weekly ones going out once at the terminal's hour.</summary>
public sealed class ReportMailApiTests(YardsyncsApiFactory factory) : ApiTestBase(factory)
{
    private string Subscriptions => $"{Terminal}/report-subscriptions";

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Mailbox : INotificationSender
    {
        public NotificationChannel Channel => NotificationChannel.Email;
        public string Provider => "Test";
        public List<(string To, RenderedMessage Message)> Sent { get; } = [];

        public Task<string?> SendAsync(string recipient, RenderedMessage message, CancellationToken ct)
        {
            Sent.Add((recipient, message));
            return Task.FromResult<string?>(null);
        }
    }

    [Fact]
    public async Task Planners_choose_who_gets_the_reports()
    {
        if (!Enabled) return;

        var planner = await As(DemoDataSeeder.PlannerEmail);
        var added = await planner.PostAsJsonAsync(Subscriptions, new { email = " Ops.Manager@Demo-Terminal.example ", language = "ar", daily = true, weekly = true });
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        var view = (await Read<AdminResponse<ReportSubscriptionView>>(added)).Data;
        Assert.Equal(("ops.manager@demo-terminal.example", "ar", true, true), (view.Email, view.Language, view.Daily, view.Weekly));

        // The same address again changes what it gets.
        Assert.Equal(HttpStatusCode.OK, (await planner.PostAsJsonAsync(Subscriptions, new { email = "ops.manager@demo-terminal.example", language = "en", daily = false, weekly = true })).StatusCode);
        var list = await Read<List<ReportSubscriptionView>>(await planner.GetAsync(Subscriptions));
        Assert.Equal((false, "en"), (Assert.Single(list).Daily, list[0].Language));

        Assert.Equal(HttpStatusCode.BadRequest, (await planner.PostAsJsonAsync(Subscriptions, new { email = "not an address", daily = true })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await planner.PostAsJsonAsync(Subscriptions, new { email = "a@b.example", language = "fr", daily = true })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await planner.PostAsJsonAsync(Subscriptions, new { email = "a@b.example", daily = false, weekly = false })).StatusCode);

        var company = await As(DemoDataSeeder.DispatcherEmail);
        Assert.Equal(HttpStatusCode.Forbidden, (await company.GetAsync(Subscriptions)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await planner.DeleteAsync($"{Subscriptions}/{view.SubscriptionId}")).StatusCode);
        Assert.Empty(await Read<List<ReportSubscriptionView>>(await planner.GetAsync(Subscriptions)));
    }

    [Fact]
    public async Task The_daily_report_goes_out_once_at_the_report_hour_with_the_excel_file_and_the_weekly_one_on_its_day()
    {
        if (!Enabled) return;

        var planner = await As(DemoDataSeeder.PlannerEmail);
        Assert.Equal(HttpStatusCode.OK, (await planner.PutAsJsonAsync($"{Terminal}/settings",
            new { values = new Dictionary<string, int> { ["report_hour"] = 6, ["report_weekday"] = 0 }, reason = (string?)null })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await planner.PostAsJsonAsync(Subscriptions, new { email = "ops@demo-terminal.example", language = "ar", daily = true, weekly = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await planner.PostAsJsonAsync(Subscriptions, new { email = "gm@demo-terminal.example", language = "en", daily = false, weekly = true })).StatusCode);

        // Sunday 2026-10-18, Riyadh: 05:30 is before the hour, 06:10 after it.
        var clock = new Clock(new DateTimeOffset(2026, 10, 18, 5, 30, 0, TimeSpan.FromHours(3)));
        var mailbox = new Mailbox();
        using var scope = Factory.Services.CreateScope();
        var p = scope.ServiceProvider;
        var service = new ReportMailService(p.GetRequiredService<YardsyncsDbContext>(), p.GetRequiredService<KpiService>(),
            p.GetRequiredService<CompanyReportService>(), p.GetRequiredService<ReportExportService>(), new NotificationSenders([mailbox]),
            clock, NullLogger<ReportMailService>.Instance);

        Assert.Equal(0, await service.SendDueAsync());
        clock.Now = clock.Now.AddMinutes(40);
        Assert.Equal(3, await service.SendDueAsync()); // ops: daily + weekly; gm: weekly
        Assert.Equal(0, await service.SendDueAsync()); // once a day

        var daily = Assert.Single(mailbox.Sent, m => m.To == "ops@demo-terminal.example" && m.Message.Subject.Contains("تقرير يوم"));
        Assert.Contains("أمس", daily.Message.Body);
        var file = Assert.Single(daily.Message.Attachments);
        Assert.Equal("report-2026-10-17.xlsx", file.FileName);
        Assert.NotEmpty(file.Content);
        var weekly = Assert.Single(mailbox.Sent, m => m.To == "gm@demo-terminal.example");
        Assert.StartsWith("Weekly report", weekly.Message.Subject);
        Assert.Equal("report-2026-10-11-2026-10-17.xlsx", Assert.Single(weekly.Message.Attachments).FileName);

        // Monday: the daily one again, no weekly one.
        clock.Now = clock.Now.AddDays(1);
        Assert.Equal(1, await service.SendDueAsync());
    }
}
