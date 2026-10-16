using System.Net;
using System.Text;
using Yardsyncs.Application.Notifications;
using Yardsyncs.Domain;
using Yardsyncs.Infrastructure.Notifications;

namespace Yardsyncs.Infrastructure.Tests;

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

    /// <summary>Answers each request with the next canned response and keeps what was sent.</summary>
    private sealed class Script(params (HttpStatusCode Status, string Body)[] answers) : HttpMessageHandler
    {
        private int _next;
        public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request, await request.Content!.ReadAsStringAsync(ct)));
            var (status, body) = answers[Math.Min(_next++, answers.Length - 1)];
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static (FcmOptions Options, System.Security.Cryptography.RSA Key) Fcm()
    {
        var key = System.Security.Cryptography.RSA.Create(2048);
        // As it arrives from an environment variable: one line with \n escapes.
        var pem = key.ExportPkcs8PrivateKeyPem().Replace("\n", "\\n");
        return (new FcmOptions { ProjectId = "yardsyncs-demo", ClientEmail = "push@yardsyncs-demo.iam.gserviceaccount.com", PrivateKey = pem }, key);
    }

    [Fact]
    public async Task Fcm_signs_in_with_the_service_account_and_sends_to_the_app()
    {
        var (options, key) = Fcm();
        var server = new Script(
            (HttpStatusCode.OK, """{"access_token":"ya29.token","expires_in":3599,"token_type":"Bearer"}"""),
            (HttpStatusCode.OK, """{"name":"projects/yardsyncs-demo/messages/0:1"}"""));
        var push = new FcmPushSender(new HttpClient(server), options, TimeProvider.System);
        var message = Message with { Data = new Dictionary<string, string> { ["template"] = "GATE_IN", ["appointmentNo"] = "DEMO-1" } };

        Assert.Equal("projects/yardsyncs-demo/messages/0:1", await push.SendAsync("device-token-1", message, CancellationToken.None));

        // 1. Sign-in: a JWT signed with the service account key.
        var (signIn, form) = server.Requests[0];
        Assert.Equal("https://oauth2.googleapis.com/token", signIn.RequestUri!.ToString());
        var assertion = WebUtility.UrlDecode(form.Split('&').Single(p => p.StartsWith("assertion=", StringComparison.Ordinal))["assertion=".Length..]);
        var parts = assertion.Split('.');
        static byte[] B64(string s) => Convert.FromBase64String(s.Replace('-', '+').Replace('_', '/').PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
        Assert.True(key.VerifyData(Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), B64(parts[2]),
            System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1));
        using (var claims = System.Text.Json.JsonDocument.Parse(B64(parts[1])))
        {
            Assert.Equal(options.ClientEmail, claims.RootElement.GetProperty("iss").GetString());
            Assert.Equal("https://www.googleapis.com/auth/firebase.messaging", claims.RootElement.GetProperty("scope").GetString());
        }

        // 2. The message, with the token from the sign-in.
        var (send, body) = server.Requests[1];
        Assert.Equal("https://fcm.googleapis.com/v1/projects/yardsyncs-demo/messages:send", send.RequestUri!.ToString());
        Assert.Equal("ya29.token", send.Headers.Authorization!.Parameter);
        using var json = System.Text.Json.JsonDocument.Parse(body);
        var m = json.RootElement.GetProperty("message");
        Assert.Equal("device-token-1", m.GetProperty("token").GetString());
        Assert.Equal(Message.Body, m.GetProperty("notification").GetProperty("body").GetString());
        Assert.Equal("GATE_IN", m.GetProperty("data").GetProperty("template").GetString());
        Assert.Equal("HIGH", m.GetProperty("android").GetProperty("priority").GetString());

        // The sign-in is reused for the next message.
        await push.SendAsync("device-token-2", Message, CancellationToken.None);
        Assert.Equal(3, server.Requests.Count);
    }

    [Fact]
    public async Task Fcm_reports_a_removed_app_and_retries_outages()
    {
        var (options, _) = Fcm();
        const string signIn = """{"access_token":"t","expires_in":3600}""";

        var gone = new FcmPushSender(new HttpClient(new Script((HttpStatusCode.OK, signIn),
            (HttpStatusCode.NotFound, """{"error":{"code":404,"message":"Requested entity was not found.","status":"NOT_FOUND","details":[{"@type":"type.googleapis.com/google.firebase.fcm.v1.FcmError","errorCode":"UNREGISTERED"}]}}"""))),
            options, TimeProvider.System);
        var ex = await Assert.ThrowsAsync<NotificationSendException>(() => gone.SendAsync("old-token", Message, CancellationToken.None));
        Assert.True(ex.InvalidRecipient);
        Assert.True(ex.Permanent);
        Assert.Contains("UNREGISTERED", ex.Message);

        var busy = new FcmPushSender(new HttpClient(new Script((HttpStatusCode.OK, signIn),
            (HttpStatusCode.ServiceUnavailable, """{"error":{"code":503,"message":"The service is currently unavailable.","status":"UNAVAILABLE"}}"""))),
            options, TimeProvider.System);
        var outage = await Assert.ThrowsAsync<NotificationSendException>(() => busy.SendAsync("token", Message, CancellationToken.None));
        Assert.False(outage.Permanent);

        var unset = new FcmPushSender(new HttpClient(new Script((HttpStatusCode.OK, signIn))), new FcmOptions(), TimeProvider.System);
        Assert.True((await Assert.ThrowsAsync<NotificationSendException>(() => unset.SendAsync("token", Message, CancellationToken.None))).Permanent);
    }
}
