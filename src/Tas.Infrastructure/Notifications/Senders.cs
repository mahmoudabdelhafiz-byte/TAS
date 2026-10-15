using System.Net;
using System.Net.Http.Headers;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Tas.Application.Notifications;
using Tas.Domain;

namespace Tas.Infrastructure.Notifications;

/// <summary>Delivery failed. <see cref="Permanent"/> = retrying won't help (bad number, rejected sender, no address).</summary>
public sealed class NotificationSendException(string message, bool permanent, Exception? inner = null) : Exception(message, inner)
{
    public bool Permanent { get; } = permanent;
}

/// <summary>Sends one rendered message on one channel; returns the provider's message id.</summary>
public interface INotificationSender
{
    NotificationChannel Channel { get; }

    /// <summary>Provider name stored with failures, e.g. "Twilio".</summary>
    string Provider { get; }

    /// <exception cref="NotificationSendException">The provider refused or could not be reached.</exception>
    Task<string?> SendAsync(string recipient, RenderedMessage message, CancellationToken ct);
}

/// <summary>The sender per channel, as configured.</summary>
public sealed class NotificationSenders(IEnumerable<INotificationSender> senders)
{
    private readonly Dictionary<NotificationChannel, INotificationSender> _byChannel = senders.ToDictionary(s => s.Channel);

    public INotificationSender? For(NotificationChannel channel) => _byChannel.GetValueOrDefault(channel);
}

/// <summary>Writes messages to the log instead of sending them — the default until a provider is configured.</summary>
public sealed class LogSender(NotificationChannel channel, ILogger<LogSender> logger) : INotificationSender
{
    public NotificationChannel Channel => channel;
    public string Provider => "Log";

    public Task<string?> SendAsync(string recipient, RenderedMessage message, CancellationToken ct)
    {
        logger.LogInformation("[{Channel} not configured — logged only] to {Recipient}: {Body}", channel, recipient, message.Body);
        return Task.FromResult<string?>($"log-{Guid.NewGuid():N}");
    }
}

/// <summary>Twilio Programmable Messaging — SMS, or WhatsApp with the "whatsapp:" address prefix.</summary>
public sealed class TwilioSender(HttpClient http, TwilioOptions options, NotificationChannel channel) : INotificationSender
{
    public NotificationChannel Channel => channel;
    public string Provider => "Twilio";

    public async Task<string?> SendAsync(string recipient, RenderedMessage message, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(options.AccountSid) || string.IsNullOrEmpty(options.AuthToken))
            throw new NotificationSendException("Twilio is not configured (AccountSid / AuthToken).", permanent: true);

        var whatsapp = channel == NotificationChannel.Whatsapp;
        var from = whatsapp ? options.WhatsappFrom : options.SmsFrom;
        if (string.IsNullOrEmpty(from))
            throw new NotificationSendException($"Twilio {(whatsapp ? "WhatsappFrom" : "SmsFrom")} is not configured.", permanent: true);

        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"{options.BaseUrl.TrimEnd('/')}/2010-04-01/Accounts/{Uri.EscapeDataString(options.AccountSid)}/Messages.json")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["To"] = whatsapp ? $"whatsapp:{recipient}" : recipient,
                ["From"] = whatsapp ? $"whatsapp:{from}" : from,
                ["Body"] = message.Body,
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.AccountSid}:{options.AuthToken}")));

        var (status, body) = await ProviderHttp.SendAsync(http, request, "Twilio", ct);
        using var json = ProviderHttp.ParseJson(body);
        if ((int)status is >= 200 and < 300)
            return json?.RootElement.TryGetProperty("sid", out var sid) == true ? sid.GetString() : null;

        var error = json?.RootElement.TryGetProperty("message", out var m) == true ? m.GetString() : body;
        throw new NotificationSendException($"Twilio refused the message ({(int)status}): {error}", ProviderHttp.IsPermanent(status));
    }
}

/// <summary>Unifonic SMS (Saudi provider) — REST "SMS/messages".</summary>
public sealed class UnifonicSmsSender(HttpClient http, UnifonicOptions options) : INotificationSender
{
    public NotificationChannel Channel => NotificationChannel.Sms;
    public string Provider => "Unifonic";

    public async Task<string?> SendAsync(string recipient, RenderedMessage message, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(options.AppSid) || string.IsNullOrEmpty(options.SenderId))
            throw new NotificationSendException("Unifonic is not configured (AppSid / SenderId).", permanent: true);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{options.BaseUrl.TrimEnd('/')}/rest/SMS/messages")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["AppSid"] = options.AppSid,
                ["SenderID"] = options.SenderId,
                ["Recipient"] = recipient.TrimStart('+'), // international format without "+"
                ["Body"] = message.Body,
                ["responseType"] = "JSON",
            }),
        };

        var (status, body) = await ProviderHttp.SendAsync(http, request, "Unifonic", ct);
        using var json = ProviderHttp.ParseJson(body);
        var root = json?.RootElement;
        var success = root?.TryGetProperty("success", out var s) == true
                      && (s.ValueKind == JsonValueKind.True || (s.ValueKind == JsonValueKind.String && s.GetString() == "true"));

        if ((int)status is >= 200 and < 300 && success)
        {
            return root!.Value.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
                   && data.TryGetProperty("MessageID", out var id)
                ? id.ToString()
                : null;
        }

        var error = root?.TryGetProperty("message", out var m) == true ? m.GetString() : body;
        var permanent = (int)status is >= 200 and < 300 || ProviderHttp.IsPermanent(status); // a 200 with success=false is a refusal
        throw new NotificationSendException($"Unifonic refused the message ({(int)status}): {error}", permanent);
    }
}

/// <summary>Email over SMTP (Office 365, Gmail, SES, the terminal's own mail server…).</summary>
public sealed class SmtpEmailSender(SmtpOptions options) : INotificationSender
{
    public NotificationChannel Channel => NotificationChannel.Email;
    public string Provider => "Smtp";

    public async Task<string?> SendAsync(string recipient, RenderedMessage message, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(options.Host) || string.IsNullOrEmpty(options.From))
            throw new NotificationSendException("SMTP is not configured (Host / From).", permanent: true);

        using var mail = new MailMessage(new MailAddress(options.From, options.FromName), new MailAddress(recipient))
        {
            Subject = message.Subject,
            Body = message.Body,
            BodyEncoding = Encoding.UTF8,
            SubjectEncoding = Encoding.UTF8,
        };
        using var smtp = new SmtpClient(options.Host, options.Port) { EnableSsl = options.EnableSsl };
        if (!string.IsNullOrEmpty(options.Username))
            smtp.Credentials = new NetworkCredential(options.Username, options.Password);

        try
        {
            await smtp.SendMailAsync(mail, ct);
            return null;
        }
        catch (SmtpFailedRecipientException ex)
        {
            throw new NotificationSendException($"The mail server refused {recipient}: {ex.Message}", permanent: true, ex);
        }
        catch (SmtpException ex)
        {
            throw new NotificationSendException($"SMTP error: {ex.Message}", permanent: false, ex);
        }
    }
}

internal static class ProviderHttp
{
    public static async Task<(HttpStatusCode Status, string Body)> SendAsync(HttpClient http, HttpRequestMessage request, string provider, CancellationToken ct)
    {
        try
        {
            using var response = await http.SendAsync(request, ct);
            return (response.StatusCode, await response.Content.ReadAsStringAsync(ct));
        }
        catch (HttpRequestException ex)
        {
            throw new NotificationSendException($"{provider} could not be reached: {ex.Message}", permanent: false, ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new NotificationSendException($"{provider} did not answer in time.", permanent: false, ex);
        }
    }

    public static JsonDocument? ParseJson(string body)
    {
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>4xx except 408 / 429 means the request itself is wrong — retrying won't help.</summary>
    public static bool IsPermanent(HttpStatusCode status) =>
        (int)status is >= 400 and < 500 && status is not (HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests);
}
