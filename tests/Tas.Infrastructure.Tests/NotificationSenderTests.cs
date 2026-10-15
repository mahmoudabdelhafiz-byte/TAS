using System.Net;
using System.Text;
using Tas.Application.Notifications;
using Tas.Domain;
using Tas.Infrastructure.Notifications;

namespace Tas.Infrastructure.Tests;

/// <summary>SMS / WhatsApp providers against a fake HTTP server (no provider account or database needed).</summary>
public class NotificationSenderTests
{
    private static readonly RenderedMessage Message = new("Yardsyncs — test", "Yardsyncs: الموعد DEMO-1 موقوف");

    private sealed class FakeServer(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public Dictionary<string, string> Form { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            var content = await request.Content!.ReadAsStringAsync(ct);
            foreach (var pair in content.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = pair.Split('=', 2);
                Form[WebUtility.UrlDecode(kv[0])] = WebUtility.UrlDecode(kv[1]);
            }
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static readonly TwilioOptions Twilio = new()
    {
        AccountSid = "AC123", AuthToken = "token", SmsFrom = "Yardsyncs", WhatsappFrom = "+966511111111",
    };

    [Fact]
    public async Task Twilio_sms_and_whatsapp_requests()
    {
        var server = new FakeServer(HttpStatusCode.Created, """{"sid":"SM42","status":"queued"}""");
        var sms = new TwilioSender(new HttpClient(server), Twilio, NotificationChannel.Sms);

        Assert.Equal("SM42", await sms.SendAsync("+966500000001", Message, CancellationToken.None));
        Assert.Equal("https://api.twilio.com/2010-04-01/Accounts/AC123/Messages.json", server.Request!.RequestUri!.ToString());
        Assert.Equal("AC123:token", Encoding.UTF8.GetString(Convert.FromBase64String(server.Request.Headers.Authorization!.Parameter!)));
        Assert.Equal("+966500000001", server.Form["To"]);
        Assert.Equal("Yardsyncs", server.Form["From"]);
        Assert.Equal(Message.Body, server.Form["Body"]);

        var waServer = new FakeServer(HttpStatusCode.Created, """{"sid":"SM43"}""");
        await new TwilioSender(new HttpClient(waServer), Twilio, NotificationChannel.Whatsapp).SendAsync("+966500000001", Message, CancellationToken.None);
        Assert.Equal("whatsapp:+966500000001", waServer.Form["To"]);
        Assert.Equal("whatsapp:+966511111111", waServer.Form["From"]);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, true)]          // e.g. invalid number
    [InlineData(HttpStatusCode.TooManyRequests, false)]
    [InlineData(HttpStatusCode.ServiceUnavailable, false)]
    public async Task Twilio_errors_are_permanent_or_retryable(HttpStatusCode status, bool permanent)
    {
        var sender = new TwilioSender(new HttpClient(new FakeServer(status, """{"code":21211,"message":"Invalid 'To' Phone Number"}""")), Twilio, NotificationChannel.Sms);

        var ex = await Assert.ThrowsAsync<NotificationSendException>(() => sender.SendAsync("+1", Message, CancellationToken.None));
        Assert.Equal(permanent, ex.Permanent);
        Assert.Contains("Invalid 'To' Phone Number", ex.Message);
    }

    [Fact]
    public async Task Unifonic_request_success_and_refusal()
    {
        var options = new UnifonicOptions { AppSid = "app-1", SenderId = "Yardsyncs" };

        var server = new FakeServer(HttpStatusCode.OK, """{"success":true,"message":"","errorCode":"ER-00","data":{"MessageID":987654}}""");
        var id = await new UnifonicSmsSender(new HttpClient(server), options).SendAsync("+966500000001", Message, CancellationToken.None);
        Assert.Equal("987654", id);
        Assert.Equal("https://el.cloud.unifonic.com/rest/SMS/messages", server.Request!.RequestUri!.ToString());
        Assert.Equal("966500000001", server.Form["Recipient"]); // no "+"
        Assert.Equal("app-1", server.Form["AppSid"]);
        Assert.Equal("Yardsyncs", server.Form["SenderID"]);
        Assert.Equal(Message.Body, server.Form["Body"]);

        var refused = new FakeServer(HttpStatusCode.OK, """{"success":"false","message":"Sender ID not approved","errorCode":"ER-05"}""");
        var ex = await Assert.ThrowsAsync<NotificationSendException>(() =>
            new UnifonicSmsSender(new HttpClient(refused), options).SendAsync("+966500000001", Message, CancellationToken.None));
        Assert.True(ex.Permanent);
        Assert.Contains("Sender ID not approved", ex.Message);

        var down = new FakeServer(HttpStatusCode.BadGateway, "<html>bad gateway</html>");
        Assert.False((await Assert.ThrowsAsync<NotificationSendException>(() =>
            new UnifonicSmsSender(new HttpClient(down), options).SendAsync("+966500000001", Message, CancellationToken.None))).Permanent);
    }

    [Fact]
    public async Task Unconfigured_providers_fail_permanently()
    {
        var http = new HttpClient(new FakeServer(HttpStatusCode.OK, "{}"));

        Assert.True((await Assert.ThrowsAsync<NotificationSendException>(() =>
            new TwilioSender(http, new TwilioOptions(), NotificationChannel.Sms).SendAsync("+9665", Message, CancellationToken.None))).Permanent);
        Assert.True((await Assert.ThrowsAsync<NotificationSendException>(() =>
            new UnifonicSmsSender(http, new UnifonicOptions()).SendAsync("+9665", Message, CancellationToken.None))).Permanent);
        Assert.True((await Assert.ThrowsAsync<NotificationSendException>(() =>
            new SmtpEmailSender(new SmtpOptions()).SendAsync("a@b.example", Message, CancellationToken.None))).Permanent);
    }
}
