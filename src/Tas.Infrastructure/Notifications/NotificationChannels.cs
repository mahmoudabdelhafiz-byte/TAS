using System.Net;
using System.Net.Http.Headers;
using System.Net.Mail;
using System.Net.Mime;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tas.Application.Notifications;
using Tas.Domain;

namespace Tas.Infrastructure.Notifications;

/// <param name="To">Mobile number (as stored, +966…) or email address.</param>
public sealed record OutgoingMessage(long NotificationId, NotificationChannel Channel, string To, RenderedMessage Message);

/// <summary>Result of one delivery attempt. <paramref name="Permanent"/> failures are not retried.</summary>
public sealed record DeliveryResult(bool Ok, string? ProviderRef, string? Error, bool Permanent)
{
    public static DeliveryResult Sent(string? providerRef) => new(true, providerRef, null, false);
    public static DeliveryResult Retry(string error) => new(false, null, error, false);
    public static DeliveryResult Fail(string error) => new(false, null, error, true);
}

/// <summary>A delivery provider for one channel (SMS gateway, SMTP server, push service…).</summary>
public interface INotificationChannel
{
    NotificationChannel Channel { get; }
    Task<DeliveryResult> SendAsync(OutgoingMessage message, CancellationToken ct);
}

// ====================================================================== options

public sealed class NotificationOptions
{
    public bool Enabled { get; set; } = true;
    public double IntervalSeconds { get; set; } = 30;
    public int BatchSize { get; set; } = 50;
    public int MaxAttempts { get; set; } = 5;

    /// <summary>Queued messages older than this are skipped instead of sent late.</summary>
    public double MaxAgeHours { get; set; } = 12;

    /// <summary>Reminder this many hours before the slot; 0 turns reminders off.</summary>
    public double ReminderHoursBefore { get; set; } = 2;

    /// <summary>A sender owns a claimed message this long; if it crashes, another sender retries afterwards.</summary>
    public double LeaseMinutes { get; set; } = 5;

    /// <summary>Address of the web portal, e.g. https://tas.terminal.example — password links point to it.</summary>
    public string? PortalUrl { get; set; }

    public SmsOptions Sms { get; set; } = new();
    public EmailOptions Email { get; set; } = new();
}

public sealed class SmsOptions
{
    /// <summary>None, Log (development: write to the log only) or Http (any HTTP SMS gateway).</summary>
    public string Provider { get; set; } = "None";

    public HttpSmsOptions Http { get; set; } = new();
}

/// <summary>
/// A generic HTTP SMS gateway: most providers (Unifonic, Taqnyat, Msegat, Twilio…) take one POST with the
/// number, text and sender name. Placeholders in <see cref="Body"/> and <see cref="Url"/>: {to} {text} {sender}.
/// </summary>
public sealed class HttpSmsOptions
{
    public string Url { get; set; } = "";
    public string Method { get; set; } = "POST";

    /// <summary>"json" (values JSON-escaped) or "form" (application/x-www-form-urlencoded, values URL-encoded).</summary>
    public string BodyFormat { get; set; } = "json";

    /// <summary>e.g. {"recipients":["{to}"],"body":"{text}","sender":"{sender}"}</summary>
    public string Body { get; set; } = "";

    /// <summary>Extra headers, e.g. Authorization: "Bearer …".</summary>
    public Dictionary<string, string> Headers { get; set; } = [];

    /// <summary>Registered sender name (Saudi sender IDs must be registered with the CST).</summary>
    public string Sender { get; set; } = "";

    public PhoneNumberFormat NumberFormat { get; set; } = PhoneNumberFormat.NoPlus;

    /// <summary>Dotted path to the provider's message id in a JSON response, e.g. "messageId" or "data.MessageID".</summary>
    public string? MessageIdPath { get; set; }

    public int TimeoutSeconds { get; set; } = 15;
}

public sealed class EmailOptions
{
    /// <summary>None, Log (development) or Smtp.</summary>
    public string Provider { get; set; } = "None";

    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public bool EnableSsl { get; set; } = true;
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string From { get; set; } = "";
    public string FromName { get; set; } = "TAS";
    public int TimeoutSeconds { get; set; } = 30;
}

// ====================================================================== log (development)

/// <summary>Writes the rendered message to the log instead of sending it. Default in development.</summary>
public sealed class LogNotificationChannel(NotificationChannel channel, ILogger<LogNotificationChannel> logger) : INotificationChannel
{
    public NotificationChannel Channel => channel;

    public Task<DeliveryResult> SendAsync(OutgoingMessage message, CancellationToken ct)
    {
        logger.LogInformation(
            "[{Channel} not sent — log provider] #{Id} to {To} ({Language}){Subject}\n{Body}",
            channel, message.NotificationId, message.To, message.Message.Language,
            message.Message.Subject is null ? "" : $" \"{message.Message.Subject}\"", message.Message.Body);
        return Task.FromResult(DeliveryResult.Sent($"log-{message.NotificationId}"));
    }
}

// ====================================================================== SMS over HTTP

public sealed class HttpSmsChannel(HttpClient http, IOptions<NotificationOptions> options) : INotificationChannel
{
    public NotificationChannel Channel => NotificationChannel.Sms;

    public async Task<DeliveryResult> SendAsync(OutgoingMessage message, CancellationToken ct)
    {
        var o = options.Value.Sms.Http;
        if (string.IsNullOrWhiteSpace(o.Url))
            return DeliveryResult.Fail("Notifications:Sms:Http:Url is not configured.");

        var to = PhoneNumbers.Format(message.To, o.NumberFormat);
        if (to is null)
            return DeliveryResult.Fail($"'{message.To}' is not an international mobile number.");

        using var request = BuildRequest(o, to, message.Message.Body);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, o.TimeoutSeconds)));

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return DeliveryResult.Retry("SMS gateway timed out.");
        }
        catch (HttpRequestException ex)
        {
            return DeliveryResult.Retry($"SMS gateway unreachable: {ex.Message}");
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            if (response.IsSuccessStatusCode)
                return DeliveryResult.Sent(ReadMessageId(body, o.MessageIdPath));

            var error = $"SMS gateway answered {(int)response.StatusCode}: {Shorten(body)}";
            var transient = response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout
                            || (int)response.StatusCode >= 500;
            return transient ? DeliveryResult.Retry(error) : DeliveryResult.Fail(error);
        }
    }

    internal static HttpRequestMessage BuildRequest(HttpSmsOptions o, string to, string text)
    {
        var url = o.Url
            .Replace("{to}", Uri.EscapeDataString(to))
            .Replace("{text}", Uri.EscapeDataString(text))
            .Replace("{sender}", Uri.EscapeDataString(o.Sender));
        var request = new HttpRequestMessage(new HttpMethod(o.Method), url);

        if (!string.IsNullOrEmpty(o.Body))
        {
            var form = string.Equals(o.BodyFormat, "form", StringComparison.OrdinalIgnoreCase);
            Func<string, string> escape = form ? Uri.EscapeDataString : JsonEscape;
            var content = o.Body
                .Replace("{to}", escape(to))
                .Replace("{text}", escape(text))
                .Replace("{sender}", escape(o.Sender));
            request.Content = new StringContent(content, Encoding.UTF8, form ? "application/x-www-form-urlencoded" : "application/json");
        }

        foreach (var (name, value) in o.Headers)
        {
            if (string.Equals(name, "Authorization", StringComparison.OrdinalIgnoreCase))
                request.Headers.Authorization = AuthenticationHeaderValue.Parse(value);
            else
                request.Headers.TryAddWithoutValidation(name, value);
        }

        return request;
    }

    /// <summary>Escapes a value for use inside a JSON string literal (the template supplies the quotes).</summary>
    internal static string JsonEscape(string value)
    {
        var quoted = JsonSerializer.Serialize(value, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        return quoted[1..^1];
    }

    internal static string? ReadMessageId(string body, string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(body))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var element = doc.RootElement;
            foreach (var part in path.Split('.'))
            {
                if (element.ValueKind == JsonValueKind.Array && int.TryParse(part, out var index) && index < element.GetArrayLength())
                    element = element[index];
                else if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(part, out var child))
                    element = child;
                else
                    return null;
            }

            var id = element.ValueKind == JsonValueKind.String ? element.GetString() : element.GetRawText();
            return id is null ? null : Shorten(id, 100);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Shorten(string value, int max = 200) => value.Length <= max ? value : value[..max];
}

// ====================================================================== email over SMTP

public sealed class SmtpEmailChannel(IOptions<NotificationOptions> options) : INotificationChannel
{
    public NotificationChannel Channel => NotificationChannel.Email;

    public async Task<DeliveryResult> SendAsync(OutgoingMessage message, CancellationToken ct)
    {
        var o = options.Value.Email;
        if (string.IsNullOrWhiteSpace(o.Host) || string.IsNullOrWhiteSpace(o.From))
            return DeliveryResult.Fail("Notifications:Email:Host / From are not configured.");

        MailAddress to;
        try
        {
            to = new MailAddress(message.To);
        }
        catch (FormatException)
        {
            return DeliveryResult.Fail($"'{message.To}' is not an email address.");
        }

        using var mail = BuildMail(o, to, message.Message);
        using var client = new SmtpClient(o.Host, o.Port)
        {
            EnableSsl = o.EnableSsl,
            Timeout = o.TimeoutSeconds * 1000,
            DeliveryMethod = SmtpDeliveryMethod.Network,
        };
        if (!string.IsNullOrEmpty(o.Username))
            client.Credentials = new NetworkCredential(o.Username, o.Password);

        try
        {
            await client.SendMailAsync(mail, ct);
            return DeliveryResult.Sent(null);
        }
        catch (SmtpFailedRecipientException ex) when (ex.StatusCode is SmtpStatusCode.MailboxUnavailable or SmtpStatusCode.MailboxNameNotAllowed or SmtpStatusCode.UserNotLocalTryAlternatePath)
        {
            return DeliveryResult.Fail($"Mailbox rejected: {ex.Message}");
        }
        catch (SmtpException ex)
        {
            return DeliveryResult.Retry($"SMTP error: {ex.Message}");
        }
    }

    /// <summary>Plain text plus an HTML part that sets the text direction (Arabic / Urdu read right to left).</summary>
    internal static MailMessage BuildMail(EmailOptions o, MailAddress to, RenderedMessage message)
    {
        var mail = new MailMessage
        {
            From = new MailAddress(o.From, o.FromName, Encoding.UTF8),
            Subject = message.Subject ?? "TAS",
            SubjectEncoding = Encoding.UTF8,
            Body = message.Body,
            BodyEncoding = Encoding.UTF8,
            IsBodyHtml = false,
        };
        mail.To.Add(to);

        var dir = message.RightToLeft ? "rtl" : "ltr";
        var html = $"<!doctype html><html lang=\"{message.Language}\" dir=\"{dir}\"><body style=\"font-family:Tahoma,Arial,sans-serif;font-size:15px;line-height:1.6\">"
                   + string.Join("<br>", message.Body.Split('\n').Select(WebUtility.HtmlEncode))
                   + "</body></html>";
        mail.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(html, Encoding.UTF8, MediaTypeNames.Text.Html));
        return mail;
    }
}
