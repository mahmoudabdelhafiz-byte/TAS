using System.Net;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Tas.Application.Notifications;
using Tas.Domain;
using Tas.Infrastructure.Notifications;

namespace Tas.Infrastructure.Tests;

/// <summary>The HTTP SMS gateway and the email builder against a fake server (no database needed).</summary>
public class NotificationChannelTests
{
    private sealed class FakeGateway(Func<HttpRequestMessage, HttpResponseMessage> handle) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }
        public int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return handle(request);
        }
    }

    private static HttpSmsChannel Channel(FakeGateway gateway, Action<HttpSmsOptions>? configure = null)
    {
        var options = new NotificationOptions();
        options.Sms.Http = new HttpSmsOptions
        {
            Url = "https://sms.test/send",
            Body = """{"recipients":["{to}"],"body":"{text}","sender":"{sender}"}""",
            Headers = { ["Authorization"] = "Bearer secret-token", ["X-Api-Version"] = "2" },
            Sender = "DEMO-TERM",
            NumberFormat = PhoneNumberFormat.NoPlus,
            MessageIdPath = "data.messageId",
        };
        configure?.Invoke(options.Sms.Http);
        return new HttpSmsChannel(new HttpClient(gateway), Options.Create(options));
    }

    private static OutgoingMessage Sms(string to, string text) =>
        new(1, NotificationChannel.Sms, to, new RenderedMessage(null, text, "ar", true));

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Sends_arabic_text_as_valid_json_with_auth_headers_and_reads_the_message_id()
    {
        var gateway = new FakeGateway(_ => Json(HttpStatusCode.OK, """{"data":{"messageId":"MSG-42"}}"""));
        const string text = "محطة: لا تتحرك لموعد \"DEMO-1\"\nانتظر";

        var result = await Channel(gateway).SendAsync(Sms("+966500000001", text), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("MSG-42", result.ProviderRef);
        Assert.Equal(HttpMethod.Post, gateway.LastRequest!.Method);
        Assert.Equal("Bearer secret-token", gateway.LastRequest.Headers.Authorization!.ToString());
        Assert.Equal("2", gateway.LastRequest.Headers.GetValues("X-Api-Version").Single());

        using var body = JsonDocument.Parse(gateway.LastBody!); // quotes and newlines are escaped correctly
        Assert.Equal("966500000001", body.RootElement.GetProperty("recipients")[0].GetString());
        Assert.Equal(text, body.RootElement.GetProperty("body").GetString());
        Assert.Equal("DEMO-TERM", body.RootElement.GetProperty("sender").GetString());
    }

    [Fact]
    public async Task Form_encoded_gateways_and_placeholders_in_the_url_work()
    {
        var gateway = new FakeGateway(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("OK") });
        var channel = Channel(gateway, o =>
        {
            o.Url = "https://sms.test/send?user=demo&sender={sender}";
            o.BodyFormat = "form";
            o.Body = "to={to}&msg={text}";
            o.NumberFormat = PhoneNumberFormat.International00;
            o.MessageIdPath = null;
        });

        var result = await channel.SendAsync(Sms("+966500000001", "a&b = ج"), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Null(result.ProviderRef);
        Assert.Equal("https://sms.test/send?user=demo&sender=DEMO-TERM", gateway.LastRequest!.RequestUri!.ToString());
        Assert.Equal("application/x-www-form-urlencoded", gateway.LastRequest.Content!.Headers.ContentType!.MediaType);
        Assert.Equal("to=00966500000001&msg=a%26b%20%3D%20%D8%AC", gateway.LastBody);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, false)]
    [InlineData(HttpStatusCode.BadGateway, false)]
    [InlineData(HttpStatusCode.TooManyRequests, false)]
    [InlineData(HttpStatusCode.BadRequest, true)]
    [InlineData(HttpStatusCode.Unauthorized, true)]
    public async Task Server_errors_and_rate_limits_are_retried_client_errors_are_not(HttpStatusCode status, bool permanent)
    {
        var gateway = new FakeGateway(_ => Json(status, """{"error":"nope"}"""));
        var result = await Channel(gateway).SendAsync(Sms("+966500000001", "x"), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(permanent, result.Permanent);
        Assert.Contains(((int)status).ToString(), result.Error);
    }

    [Fact]
    public async Task Unreachable_gateway_is_retried_and_a_bad_number_is_not_sent()
    {
        var down = new FakeGateway(_ => throw new HttpRequestException("connection refused"));
        var unreachable = await Channel(down).SendAsync(Sms("+966500000001", "x"), CancellationToken.None);
        Assert.False(unreachable.Ok);
        Assert.False(unreachable.Permanent);

        var gateway = new FakeGateway(_ => Json(HttpStatusCode.OK, "{}"));
        var bad = await Channel(gateway).SendAsync(Sms("0500000001", "x"), CancellationToken.None);
        Assert.False(bad.Ok);
        Assert.True(bad.Permanent);
        Assert.Equal(0, gateway.Calls);
    }

    [Theory]
    [InlineData("""{"messageId":"A1"}""", "messageId", "A1")]
    [InlineData("""{"data":{"MessageID":12345}}""", "data.MessageID", "12345")]
    [InlineData("""{"messages":[{"id":"Z9"}]}""", "messages.0.id", "Z9")]
    [InlineData("""{"other":1}""", "messageId", null)]
    [InlineData("not json", "messageId", null)]
    public void Message_id_is_read_from_the_configured_path(string body, string path, string? expected) =>
        Assert.Equal(expected, HttpSmsChannel.ReadMessageId(body, path));

    [Fact]
    public void Emails_have_a_utf8_subject_and_a_right_to_left_html_part_for_arabic()
    {
        var options = new EmailOptions { From = "no-reply@terminal.test", FromName = "Truck Appointments" };
        using var mail = SmtpEmailChannel.BuildMail(options, new MailAddress("khalid@alnoor.test"),
            new RenderedMessage("لا تتحرك: الموعد DEMO-1", "سطر 1\n<سطر 2>", "ar", true));

        Assert.Equal("لا تتحرك: الموعد DEMO-1", mail.Subject);
        Assert.Equal(Encoding.UTF8, mail.SubjectEncoding);
        Assert.Equal("khalid@alnoor.test", mail.To.Single().Address);
        Assert.Equal("سطر 1\n<سطر 2>", mail.Body);

        var html = new StreamReader(mail.AlternateViews.Single().ContentStream).ReadToEnd();
        Assert.Contains("dir=\"rtl\"", html);
        Assert.Contains("lang=\"ar\"", html);
        Assert.Contains("سطر 1<br>&lt;سطر 2&gt;", html);
    }
}
