using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Yardsyncs.Api;
using Yardsyncs.Domain;
using Yardsyncs.Infrastructure;
using Yardsyncs.Infrastructure.Booking;
using Yardsyncs.Infrastructure.Persistence;
using Yardsyncs.Infrastructure.Tos;

namespace Yardsyncs.Api.Tests;

/// <summary>Track containers without booking, and follow them until they are ready to collect.</summary>
public sealed class ContainerTrackingApiTests(YardsyncsApiFactory factory) : ApiTestBase(factory)
{
    private string Track => $"{Terminal}/containers/track";
    private string Followed => $"{Terminal}/containers/followed";

    [Fact]
    public async Task A_bill_of_lading_shows_where_each_container_stands()
    {
        if (!Enabled) return;

        var alNoor = await As(DemoDataSeeder.DispatcherEmail);
        var bl = (await Read<AdminResponse<TrackResult>>(await alNoor.PostAsJsonAsync(Track, new { blNo = "meduJD260001" }))).Data;
        Assert.Equal("MEDUJD260001", bl.BlNo);
        var byNo = bl.Containers.ToDictionary(c => c.ContainerNo);
        Assert.Equal(5, byNo.Count);
        Assert.Equal(ContainerState.Ready, byNo["MSCU1234566"].State);
        Assert.Equal(20, byNo["MSCU1234566"].SizeFt);
        Assert.Contains(byNo["MSCU1234566"].Checks, c => c.Type == "CustomsRelease" && c.Result == "Pass");
        Assert.Equal(ContainerState.OnHold, byNo["MSCU7654329"].State);
        Assert.Contains("ustoms", Assert.Single(byNo["MSCU7654329"].Problems));
        Assert.Equal(ContainerState.Ready, byNo["CMAU5100028"].State);
        Assert.NotEmpty(byNo["CMAU5100028"].Warnings);

        var list = (await Read<AdminResponse<TrackResult>>(await alNoor.PostAsJsonAsync(Track, new { containers = new[] { "mscu1234566", "ABC" } }))).Data;
        Assert.Equal(ContainerState.Invalid, list.Containers.Single(c => c.ContainerNo == "ABC").State);
        Assert.Equal(HttpStatusCode.NotFound, (await alNoor.PostAsJsonAsync(Track, new { blNo = "NOPE123" })).StatusCode);

        // Booked: the company sees its appointment, another company only that it's taken.
        var booked = await alNoor.PostAsJsonAsync(Appointments, ImportBooking());
        Assert.Equal(HttpStatusCode.Created, booked.StatusCode);
        var mine = (await Read<AdminResponse<TrackResult>>(await alNoor.PostAsJsonAsync(Track, new { containers = new[] { "MSCU1234566" } }))).Data.Containers.Single();
        Assert.Equal(ContainerState.Booked, mine.State);
        Assert.NotNull(mine.Appointment);
        var gulf = await As(DemoDataSeeder.OtherDispatcherEmail);
        var theirs = (await Read<AdminResponse<TrackResult>>(await gulf.PostAsJsonAsync(Track, new { containers = new[] { "MSCU1234566" } }))).Data.Containers.Single();
        Assert.Equal(ContainerState.Booked, theirs.State);
        Assert.Null(theirs.Appointment);
        Assert.True(theirs.BookedByOthers);
    }

    [Fact]
    public async Task Followers_get_an_email_when_the_hold_is_lifted()
    {
        if (!Enabled) return;

        var alNoor = await As(DemoDataSeeder.DispatcherEmail);
        Assert.Equal(HttpStatusCode.BadRequest, (await alNoor.PostAsJsonAsync(Followed, new { containers = new[] { "MSCU1234567" } })).StatusCode); // check digit
        var followed = (await Read<AdminResponse<List<WatchView>>>(await alNoor.PostAsJsonAsync(Followed, new { containers = new[] { "MSCU7654329" } }))).Data;
        Assert.Equal(ContainerState.OnHold, Assert.Single(followed).State);
        Assert.True((await Read<AdminResponse<TrackResult>>(await alNoor.PostAsJsonAsync(Track, new { containers = new[] { "MSCU7654329" } }))).Data.Containers.Single().Following);

        var tos = Factory.Services.GetRequiredService<IOptionsMonitor<FakeTosOptions>>().CurrentValue;
        tos.CustomsHold.Remove("MSCU7654329");
        try
        {
            using var scope = Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<YardsyncsDbContext>();
            var tracking = scope.ServiceProvider.GetRequiredService<ContainerTrackingService>();

            Assert.Equal(0, await tracking.CheckFollowedAsync()); // just checked: not due yet
            await db.ContainerWatches.ExecuteUpdateAsync(x => x.SetProperty(w => w.LastCheckedAt, DateTimeOffset.UtcNow.AddHours(-1)));
            Assert.Equal(1, await tracking.CheckFollowedAsync());

            var mail = await db.NotificationLogs.AsNoTracking().SingleAsync(n => n.TemplateCode == NotificationTemplates.ContainerReady);
            Assert.Equal(Demo.DispatcherUserId, mail.UserId);
            Assert.Equal(NotificationChannel.Email, mail.Channel);
            Assert.Contains("MSCU7654329", mail.Params);

            // Still ready on the next round: no second email.
            await db.ContainerWatches.ExecuteUpdateAsync(x => x.SetProperty(w => w.LastCheckedAt, DateTimeOffset.UtcNow.AddHours(-1)));
            Assert.Equal(0, await tracking.CheckFollowedAsync());
        }
        finally
        {
            tos.CustomsHold.Add("MSCU7654329");
        }

        Assert.Equal(ContainerState.Ready, Assert.Single(await Read<List<WatchView>>(await alNoor.GetAsync(Followed))).State);
        Assert.Equal(HttpStatusCode.NoContent, (await alNoor.DeleteAsync($"{Followed}/MSCU7654329")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alNoor.DeleteAsync($"{Followed}/MSCU7654329")).StatusCode);
        Assert.Empty(await Read<List<WatchView>>(await alNoor.GetAsync(Followed)));
    }
}
