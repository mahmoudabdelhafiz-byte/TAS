using Tas.Domain;
using Tas.Domain.Booking;

namespace Tas.Application.Notifications;

/// <summary>One failing check on one move, e.g. ("MSCU1234566", CustomsRelease).</summary>
public sealed record NotificationProblem(string Item, CheckType Check);

/// <summary>What a template needs to say about the appointment. Times are already in the terminal's local time.</summary>
public sealed record NotificationData(
    string AppointmentNo,
    string TerminalName,
    DateTime SlotStartLocal,
    DateTime SlotEndLocal,
    string? TruckPlate,
    IReadOnlyList<NotificationProblem> Problems);

/// <param name="Subject">Email subject; null for SMS / WhatsApp / push.</param>
/// <param name="Body">Plain text body.</param>
/// <param name="RightToLeft">True for Arabic and Urdu (email HTML direction).</param>
public sealed record RenderedMessage(string? Subject, string Body, string Language, bool RightToLeft);

/// <summary>How a mobile number is written in the SMS provider's request.</summary>
public enum PhoneNumberFormat
{
    /// <summary>+966500000001</summary>
    E164,

    /// <summary>966500000001 (most Saudi SMS gateways)</summary>
    NoPlus,

    /// <summary>00966500000001</summary>
    International00,
}

public static class PhoneNumbers
{
    /// <summary>Formats a stored +966… number for a provider; null if it isn't a usable international number.</summary>
    public static string? Format(string? mobile, PhoneNumberFormat format)
    {
        if (string.IsNullOrWhiteSpace(mobile))
            return null;
        var digits = new string(mobile.Where(char.IsAsciiDigit).ToArray());
        if (!mobile.TrimStart().StartsWith('+') || digits.Length is < 8 or > 15)
            return null;

        return format switch
        {
            PhoneNumberFormat.NoPlus => digits,
            PhoneNumberFormat.International00 => "00" + digits,
            _ => "+" + digits,
        };
    }
}

/// <summary>Retry back-off for transient delivery errors (provider down, timeout, rate limited).</summary>
public static class NotificationRetry
{
    private static readonly TimeSpan[] Delays =
        [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30)];

    /// <summary>Delay after the given (1-based) failed attempt.</summary>
    public static TimeSpan DelayAfter(int attempt) => Delays[Math.Clamp(attempt - 1, 0, Delays.Length - 1)];

    public static bool Exhausted(int attempts, int maxAttempts) => attempts >= maxAttempts;
}

/// <summary>Why a queued notification is no longer worth sending; null if it should go out.</summary>
public static class NotificationRelevance
{
    public static string? SkipReason(
        string template,
        AppointmentStatus status,
        DateTimeOffset slotEnd,
        bool anyItemFailing,
        bool supersededByLater,
        DateTimeOffset createdAt,
        DateTimeOffset now,
        TimeSpan maxAge)
    {
        if (now - createdAt > maxAge)
            return $"Too old to send (queued {createdAt:u}).";
        if (!Appointment.IsBeforeGate(status))
            return $"Appointment is {status}.";
        if (slotEnd <= now)
            return "The slot has passed.";

        return template switch
        {
            NotificationTemplates.StatusRed or NotificationTemplates.StatusCleared when supersededByLater =>
                "Superseded by a newer status message.",
            NotificationTemplates.StatusRed when !anyItemFailing => "Nothing is failing any more.",
            NotificationTemplates.StatusCleared when anyItemFailing => "A check is failing again.",
            NotificationTemplates.SlotReminder when status == AppointmentStatus.Arrived => "The truck has already arrived.",
            _ => null,
        };
    }
}
