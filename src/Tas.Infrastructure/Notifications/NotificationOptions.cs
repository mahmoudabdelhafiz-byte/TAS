using Tas.Domain;

namespace Tas.Infrastructure.Notifications;

/// <summary>
/// Configuration section "Notifications". Every provider defaults to "Log" (messages are written to the log, not
/// sent) until a real one is configured. Secrets belong in environment variables, e.g.
/// Notifications__Twilio__AuthToken, Notifications__Unifonic__AppSid, Notifications__Smtp__Password.
/// </summary>
public sealed class NotificationOptions
{
    public bool Enabled { get; set; } = true;
    public int IntervalSeconds { get; set; } = 30;
    public int BatchSize { get; set; } = 50;

    /// <summary>Attempts before a message is given up as FAILED (retries after 1, 5, 15, 60 min).</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>Messages not sent within this many hours of being queued are dropped (they would arrive too late).</summary>
    public int ExpireHours { get; set; } = 12;

    /// <summary>Drivers get a reminder this many hours before their slot; 0 turns reminders off.</summary>
    public double ReminderHoursBefore { get; set; } = 2;

    /// <summary>How drivers are reached: Sms or Whatsapp.</summary>
    public NotificationChannel DriverChannel { get; set; } = NotificationChannel.Sms;

    /// <summary>Log | Unifonic | Twilio</summary>
    public string SmsProvider { get; set; } = "Log";

    /// <summary>Log | Twilio</summary>
    public string WhatsappProvider { get; set; } = "Log";

    /// <summary>Log | Smtp</summary>
    public string EmailProvider { get; set; } = "Log";

    public UnifonicOptions Unifonic { get; set; } = new();
    public TwilioOptions Twilio { get; set; } = new();
    public SmtpOptions Smtp { get; set; } = new();
}

public sealed class UnifonicOptions
{
    public string BaseUrl { get; set; } = "https://el.cloud.unifonic.com";
    public string AppSid { get; set; } = "";

    /// <summary>Registered sender name, e.g. "Yardsyncs".</summary>
    public string SenderId { get; set; } = "";
}

public sealed class TwilioOptions
{
    public string BaseUrl { get; set; } = "https://api.twilio.com";
    public string AccountSid { get; set; } = "";
    public string AuthToken { get; set; } = "";

    /// <summary>SMS sender number or alphanumeric sender id.</summary>
    public string SmsFrom { get; set; } = "";

    /// <summary>WhatsApp business number in E.164, e.g. +9665XXXXXXXX.</summary>
    public string WhatsappFrom { get; set; } = "";
}

public sealed class SmtpOptions
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public bool EnableSsl { get; set; } = true;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string From { get; set; } = "";
    public string FromName { get; set; } = "Yardsyncs";
}
