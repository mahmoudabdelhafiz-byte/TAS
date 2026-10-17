using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yardsyncs.Application.Messages;
using Yardsyncs.Domain;
using Yardsyncs.Domain.Booking;
using Yardsyncs.Infrastructure;
using Yardsyncs.Infrastructure.DriverApp;
using Yardsyncs.Infrastructure.Persistence;

namespace Yardsyncs.Api.Tests;

/// <summary>Messages between the driver (app) and the control room about a visit.</summary>
public sealed class MessageApiTests(YardsyncsApiFactory factory) : ApiTestBase(factory)
{
    private const string Abdullah = "0500000001"; // demo driver, +966500000001

    private async Task<HttpClient> DriverAsync(string language)
    {
        var start = await Factory.CreateClient().PostAsJsonAsync("/api/driver/login/start", new { mobile = Abdullah, installId = "install-chat-0001" });
        var code = JsonDocument.Parse(await start.Content.ReadAsStringAsync()).RootElement.GetProperty("testCode").GetString();
        var verify = await Factory.CreateClient().PostAsJsonAsync("/api/driver/login/verify",
            new { mobile = Abdullah, installId = "install-chat-0001", code, platform = "android", appVersion = "0.1.0" });
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await Read<DriverSession>(verify)).Token);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(language);
        return client;
    }

    private async Task<string> BookWithDriverAsync()
    {
        var alNoor = await As(DemoDataSeeder.DispatcherEmail);
        var booked = await alNoor.PostAsJsonAsync(Appointments, new
        {
            slotId = SlotId,
            driverId = Demo.DriverIds[0],
            items = new[] { new { category = "IMP_PICKUP", containerNo = "MSCU1234566" } },
        });
        Assert.Equal(HttpStatusCode.Created, booked.StatusCode);
        return (await Read<Yardsyncs.Application.Booking.AppointmentDto>(booked)).AppointmentNo;
    }

    [Fact]
    public async Task The_driver_and_the_control_room_write_to_each_other_each_in_their_language()
    {
        if (!Enabled) return;

        var number = await BookWithDriverAsync();
        var driver = await DriverAsync("ur");
        var room = await As(DemoDataSeeder.TerminalAdminEmail);
        var thread = $"{Terminal}/messages/{number}";

        // The driver chooses a ready-made message in Urdu; the control room reads it in English.
        var templates = await Read<List<MessageTemplateView>>(await driver.GetAsync("/api/driver/message-templates"));
        Assert.Contains(templates, t => t.Code == "CALL_ME" && t.Type == MessageType.CallRequest && t.Text == "براہ کرم مجھے کال کریں۔");
        Assert.DoesNotContain(templates, t => t.Code == "GO_TO_TROUBLE_DESK");
        Assert.Equal(HttpStatusCode.Created, (await driver.PostAsJsonAsync($"/api/driver/appointments/{number}/messages", new { templateCode = "running_late" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await driver.PostAsJsonAsync($"/api/driver/appointments/{number}/messages", new { text = "  Traffic at the bridge  " })).StatusCode);

        var list = await Read<List<ConversationView>>(await room.GetAsync($"{Terminal}/messages"));
        var conversation = Assert.Single(list);
        Assert.Equal((number, 2, "Abdullah Al-Harbi"), (conversation.AppointmentNo, conversation.Unread, conversation.DriverName));
        Assert.Equal("Traffic at the bridge", conversation.Last.Text);

        var read = (await Read<AdminResponse<List<MessageView>>>(await room.GetAsync(thread))).Data;
        Assert.Equal(new[] { "I am running late.", "Traffic at the bridge" }, read.Select(m => m.Text));
        Assert.Equal(0, Assert.Single(await Read<List<ConversationView>>(await room.GetAsync($"{Terminal}/messages"))).Unread);

        // The control room answers with a ready-made message: kept, and sent to the driver's phone in Arabic (his language).
        var sent = await room.PostAsJsonAsync(thread, new { templateCode = "WAIT_IN_PARKING" });
        Assert.Equal(HttpStatusCode.Created, sent.StatusCode);
        Assert.False(string.IsNullOrEmpty((await Read<AdminResponse<MessageView>>(sent)).Data.SenderName));
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<YardsyncsDbContext>();
            var queued = await db.NotificationLogs.SingleAsync(n => n.TemplateCode == NotificationTemplates.ControlRoomMessage);
            Assert.Equal(Demo.DriverIds[0], queued.DriverId);
            Assert.StartsWith("يرجى الانتظار في موقف الشاحنات",
                JsonDocument.Parse(queued.Params!).RootElement.GetProperty("message").GetString());
        }

        // The app shows it unread, then in Urdu.
        var mine = await Read<DriverAppointment>(await driver.GetAsync($"/api/driver/appointments/{number}"));
        Assert.Equal(1, mine.UnreadMessages);
        var driverThread = (await Read<AdminResponse<List<MessageView>>>(await driver.GetAsync($"/api/driver/appointments/{number}/messages"))).Data;
        Assert.Equal("براہ کرم ٹرک پارکنگ میں انتظار کریں۔ ہم آپ کو بتائیں گے کب آنا ہے۔", driverThread.Last().Text);
        Assert.Equal(MessageDirection.ToDriver, driverThread.Last().Direction);
        Assert.Equal(0, (await Read<DriverAppointment>(await driver.GetAsync($"/api/driver/appointments/{number}"))).UnreadMessages);
    }

    [Fact]
    public async Task Only_the_terminals_staff_and_the_visits_driver_take_part()
    {
        if (!Enabled) return;

        var number = await BookWithDriverAsync();
        var dispatcher = await As(DemoDataSeeder.DispatcherEmail);
        Assert.Equal(HttpStatusCode.Forbidden, (await dispatcher.GetAsync($"{Terminal}/messages")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await dispatcher.PostAsJsonAsync($"{Terminal}/messages/{number}", new { text = "hi" })).StatusCode);

        var driver = await DriverAsync("en");
        Assert.Equal(HttpStatusCode.NotFound, (await driver.GetAsync("/api/driver/appointments/NOT-MINE/messages")).StatusCode);

        // A driver can't send the control room's ready-made messages; empty and too long are refused.
        var bad = await driver.PostAsJsonAsync($"/api/driver/appointments/{number}/messages", new { templateCode = "GO_TO_TROUBLE_DESK" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await driver.PostAsJsonAsync($"/api/driver/appointments/{number}/messages", new { text = " " })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await driver.PostAsJsonAsync($"/api/driver/appointments/{number}/messages", new { text = new string('x', MessageRules.MaxLength + 1) })).StatusCode);

        // A visit without a driver has no one to write to.
        var noDriver = await dispatcher.PostAsJsonAsync(Appointments, ImportBooking(container: "MSCU5523417"));
        Assert.Equal(HttpStatusCode.Created, noDriver.StatusCode);
        var other = (await Read<Yardsyncs.Application.Booking.AppointmentDto>(noDriver)).AppointmentNo;
        var room = await As(DemoDataSeeder.PlannerEmail);
        Assert.Equal(HttpStatusCode.Conflict, (await room.PostAsJsonAsync($"{Terminal}/messages/{other}", new { text = "Where are you?" })).StatusCode);
    }

    [Fact]
    public async Task The_driver_shares_the_trucks_location_and_the_control_room_sees_it()
    {
        if (!Enabled) return;

        var number = await BookWithDriverAsync();
        var driver = await DriverAsync("en");
        var shared = await driver.PostAsJsonAsync($"/api/driver/appointments/{number}/messages", new { latitude = 21.4858, longitude = 39.1925 });
        Assert.Equal(HttpStatusCode.Created, shared.StatusCode);
        Assert.Equal(MessageType.Location, (await Read<AdminResponse<MessageView>>(shared)).Data.Type);

        var room = await As(DemoDataSeeder.TerminalAdminEmail);
        room.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ar");
        var seen = Assert.Single((await Read<AdminResponse<List<MessageView>>>(await room.GetAsync($"{Terminal}/messages/{number}"))).Data);
        Assert.Equal(new[] { 39.1925, 21.4858 }, seen.Point);
        Assert.Equal("شارك موقع الشاحنة.", seen.Text);

        // The control room has no location to share.
        Assert.Equal(HttpStatusCode.BadRequest, (await room.PostAsJsonAsync($"{Terminal}/messages/{number}", new { latitude = 21.4, longitude = 39.1 })).StatusCode);
    }
}
