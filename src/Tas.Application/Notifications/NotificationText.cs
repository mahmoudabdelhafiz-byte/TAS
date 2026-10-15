using System.Globalization;
using System.Text.RegularExpressions;
using Tas.Domain;

namespace Tas.Application.Notifications;

public sealed record RenderedMessage(string Subject, string Body);

/// <summary>
/// Message texts in Arabic, English, Urdu and Hindi. Placeholders in {braces} are filled from the notification's
/// params; unknown languages fall back to English. Texts are short enough for SMS.
/// Arabic / Urdu / Hindi wording should be reviewed by native speakers before go-live.
/// </summary>
public static partial class NotificationText
{
    public const string Brand = "Yardsyncs";

    /// <summary>Parameter names used by the templates.</summary>
    public static class Keys
    {
        public const string AppointmentNo = "appointmentNo";
        public const string Slot = "slot";
        public const string Reason = "reason";
        public const string GateOpens = "gateOpens";
        public const string Items = "items";
    }

    private static readonly Dictionary<string, Dictionary<string, (string Subject, string Body)>> Templates = new()
    {
        [NotificationTemplates.StatusRed] = new()
        {
            ["en"] = ("Appointment {appointmentNo} on hold",
                "Yardsyncs: appointment {appointmentNo} ({slot}) is on hold: {reason}. Do not set off until it is cleared."),
            ["ar"] = ("الموعد {appointmentNo} موقوف",
                "Yardsyncs: الموعد {appointmentNo} ({slot}) موقوف: {reason}. لا تتحرك حتى يتم الإفراج."),
            ["ur"] = ("اپوائنٹمنٹ {appointmentNo} روکی گئی",
                "Yardsyncs: اپوائنٹمنٹ {appointmentNo} ({slot}) روکی گئی ہے: {reason}۔ کلیئر ہونے تک روانہ نہ ہوں۔"),
            ["hi"] = ("अपॉइंटमेंट {appointmentNo} रोका गया",
                "Yardsyncs: अपॉइंटमेंट {appointmentNo} ({slot}) रोका गया है: {reason}। क्लियर होने तक रवाना न हों।"),
        },
        [NotificationTemplates.StatusCleared] = new()
        {
            ["en"] = ("Appointment {appointmentNo} cleared",
                "Yardsyncs: appointment {appointmentNo} ({slot}) is cleared. You can come at your slot time."),
            ["ar"] = ("تم الإفراج عن الموعد {appointmentNo}",
                "Yardsyncs: تم الإفراج عن الموعد {appointmentNo} ({slot}). يمكنك الحضور في وقت موعدك."),
            ["ur"] = ("اپوائنٹمنٹ {appointmentNo} کلیئر",
                "Yardsyncs: اپوائنٹمنٹ {appointmentNo} ({slot}) کلیئر ہو گئی ہے۔ آپ اپنے وقت پر آ سکتے ہیں۔"),
            ["hi"] = ("अपॉइंटमेंट {appointmentNo} क्लियर",
                "Yardsyncs: अपॉइंटमेंट {appointmentNo} ({slot}) क्लियर है। आप अपने समय पर आ सकते हैं।"),
        },
        [NotificationTemplates.SlotReminder] = new()
        {
            ["en"] = ("Reminder: appointment {appointmentNo}",
                "Yardsyncs reminder: appointment {appointmentNo} at {slot}. The gate opens for you at {gateOpens}. Moves: {items}."),
            ["ar"] = ("تذكير: الموعد {appointmentNo}",
                "تذكير Yardsyncs: الموعد {appointmentNo} في {slot}. تفتح البوابة لك الساعة {gateOpens}. الحاويات: {items}."),
            ["ur"] = ("یاد دہانی: اپوائنٹمنٹ {appointmentNo}",
                "Yardsyncs یاد دہانی: اپوائنٹمنٹ {appointmentNo} وقت {slot}۔ گیٹ آپ کے لیے {gateOpens} بجے کھلے گا۔ کنٹینرز: {items}۔"),
            ["hi"] = ("रिमाइंडर: अपॉइंटमेंट {appointmentNo}",
                "Yardsyncs रिमाइंडर: अपॉइंटमेंट {appointmentNo} समय {slot}। गेट आपके लिए {gateOpens} बजे खुलेगा। कंटेनर: {items}।"),
        },
        [NotificationTemplates.SlotClosed] = new()
        {
            ["en"] = ("Slot closed: appointment {appointmentNo}",
                "Yardsyncs: the terminal closed your slot {slot} (appointment {appointmentNo}): {reason}. Please reschedule."),
            ["ar"] = ("إغلاق الفترة: الموعد {appointmentNo}",
                "Yardsyncs: أغلقت المحطة فترتك {slot} (الموعد {appointmentNo}): {reason}. يرجى إعادة الجدولة."),
            ["ur"] = ("سلاٹ بند: اپوائنٹمنٹ {appointmentNo}",
                "Yardsyncs: ٹرمینل نے آپ کا سلاٹ {slot} (اپوائنٹمنٹ {appointmentNo}) بند کر دیا ہے: {reason}۔ براہ کرم دوبارہ وقت لیں۔"),
            ["hi"] = ("स्लॉट बंद: अपॉइंटमेंट {appointmentNo}",
                "Yardsyncs: टर्मिनल ने आपका स्लॉट {slot} (अपॉइंटमेंट {appointmentNo}) बंद कर दिया है: {reason}। कृपया दोबारा समय लें।"),
        },
    };

    [GeneratedRegex(@"\{([a-zA-Z]+)\}")]
    private static partial Regex Placeholder();

    public static IReadOnlyCollection<string> TemplateCodes => Templates.Keys;

    public static IReadOnlyCollection<string> Languages => ["ar", "en", "ur", "hi"];

    public static bool IsKnown(string templateCode) => Templates.ContainsKey(templateCode);

    /// <summary>Fills the template; a missing value becomes "-" so a message is never sent with raw braces.</summary>
    public static RenderedMessage Render(string templateCode, string? language, IReadOnlyDictionary<string, string> values)
    {
        if (!Templates.TryGetValue(templateCode, out var byLanguage))
            throw new ArgumentException($"Unknown notification template '{templateCode}'.", nameof(templateCode));

        var lang = (language ?? "en").Trim().ToLowerInvariant();
        var (subject, body) = byLanguage.TryGetValue(lang, out var text) ? text : byLanguage["en"];

        string Fill(string s) => Placeholder().Replace(s, m =>
            values.TryGetValue(m.Groups[1].Value, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : "-");

        return new RenderedMessage($"{Brand} — {Fill(subject)}", Fill(body));
    }

    /// <summary>"Tue 20 Oct 10:00–11:00" in the terminal's time zone (invariant month/day names).</summary>
    public static string SlotLabel(DateTimeOffset start, DateTimeOffset end, TimeZoneInfo timeZone)
    {
        var s = TimeZoneInfo.ConvertTime(start, timeZone);
        var e = TimeZoneInfo.ConvertTime(end, timeZone);
        return $"{s.ToString("ddd dd MMM HH:mm", CultureInfo.InvariantCulture)}–{e.ToString("HH:mm", CultureInfo.InvariantCulture)}";
    }
}
