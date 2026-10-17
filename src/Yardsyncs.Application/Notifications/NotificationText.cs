using System.Globalization;
using System.Text.RegularExpressions;
using Yardsyncs.Application.Languages;
using Yardsyncs.Domain;

namespace Yardsyncs.Application.Notifications;

/// <summary>A file sent with an email (e.g. a report as Excel).</summary>
public sealed record MessageAttachment(string FileName, string ContentType, byte[] Content);

public sealed record RenderedMessage(string Subject, string Body)
{
    /// <summary>Files for email; other channels leave them out.</summary>
    public IReadOnlyList<MessageAttachment> Attachments { get; init; } = [];

    /// <summary>Extra values for app push messages (template, appointment number), so the app can react to them.</summary>
    public IReadOnlyDictionary<string, string>? Data { get; init; }
}

/// <summary>
/// Message texts in Arabic, English, Urdu and Hindi, plus whatever language packs add or correct
/// (<see cref="LanguageCatalog"/>). Placeholders in {braces} are filled from the notification's params; a language
/// without the text falls back to English. Texts are short enough for SMS.
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

        /// <summary>Where to go inside the terminal, e.g. "B2 (14.03.2) → E1".</summary>
        public const string Stops = "stops";
        public const string Container = "container";
        public const string Terminal = "terminal";

        /// <summary>A control room message, already in the driver's language.</summary>
        public const string Message = "message";
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
        [NotificationTemplates.ContainerReady] = new()
        {
            ["en"] = ("Container {container} is ready",
                "Yardsyncs: container {container} is ready to collect at {terminal}. Book a truck in the Yardsyncs portal."),
            ["ar"] = ("الحاوية {container} جاهزة",
                "Yardsyncs: الحاوية {container} جاهزة للاستلام في {terminal}. احجز شاحنة من بوابة Yardsyncs."),
            ["ur"] = ("کنٹینر {container} تیار ہے",
                "Yardsyncs: کنٹینر {container} {terminal} سے اٹھانے کے لیے تیار ہے۔ Yardsyncs پورٹل میں ٹرک بک کریں۔"),
            ["hi"] = ("कंटेनर {container} तैयार है",
                "Yardsyncs: कंटेनर {container} {terminal} से उठाने के लिए तैयार है। Yardsyncs पोर्टल में ट्रक बुक करें।"),
        },
        [NotificationTemplates.GateIn] = new()
        {
            ["en"] = ("You are in: appointment {appointmentNo}",
                "Yardsyncs: appointment {appointmentNo}, you are inside the terminal. Go to {stops}. Open Yardsyncs mobile for the map."),
            ["ar"] = ("دخلت المحطة: الموعد {appointmentNo}",
                "Yardsyncs: الموعد {appointmentNo}، أنت داخل المحطة. توجّه إلى {stops}. افتح تطبيق Yardsyncs mobile لرؤية الخريطة."),
            ["ur"] = ("آپ اندر ہیں: اپوائنٹمنٹ {appointmentNo}",
                "Yardsyncs: اپوائنٹمنٹ {appointmentNo}، آپ ٹرمینل کے اندر ہیں۔ {stops} جائیں۔ نقشے کے لیے Yardsyncs mobile کھولیں۔"),
            ["hi"] = ("आप अंदर हैं: अपॉइंटमेंट {appointmentNo}",
                "Yardsyncs: अपॉइंटमेंट {appointmentNo}, आप टर्मिनल के अंदर हैं। {stops} जाएँ। नक्शे के लिए Yardsyncs mobile खोलें।"),
        },
        [NotificationTemplates.DriverIncident] = new()
        {
            ["en"] = ("Recorded on your driver record", "Yardsyncs: the terminal recorded \"{reason}\" on appointment {appointmentNo}. See it in Yardsyncs mobile; you can dispute it there."),
            ["ar"] = ("سُجّلت مخالفة في سجلك", "Yardsyncs: سجّلت المحطة \"{reason}\" على الموعد {appointmentNo}. راجعها في تطبيق Yardsyncs mobile ويمكنك الاعتراض عليها هناك."),
            ["ur"] = ("آپ کے ریکارڈ میں درج", "Yardsyncs: ٹرمینل نے اپوائنٹمنٹ {appointmentNo} پر \"{reason}\" درج کیا ہے۔ Yardsyncs mobile میں دیکھیں؛ وہاں اعتراض کر سکتے ہیں۔"),
            ["hi"] = ("आपके रिकॉर्ड में दर्ज", "Yardsyncs: टर्मिनल ने अपॉइंटमेंट {appointmentNo} पर \"{reason}\" दर्ज किया है। Yardsyncs mobile में देखें; वहाँ आपत्ति कर सकते हैं।"),
        },
        [NotificationTemplates.ControlRoomMessage] = new()
        {
            ["en"] = ("Message from the terminal", "Yardsyncs: appointment {appointmentNo}, from the control room: {message}"),
            ["ar"] = ("رسالة من المحطة", "Yardsyncs: الموعد {appointmentNo}، من غرفة التحكم: {message}"),
            ["ur"] = ("ٹرمینل کا پیغام", "Yardsyncs: اپوائنٹمنٹ {appointmentNo}، کنٹرول روم سے: {message}"),
            ["hi"] = ("टर्मिनल का संदेश", "Yardsyncs: अपॉइंटमेंट {appointmentNo}, कंट्रोल रूम से: {message}"),
        },
    };

    /// <summary>The drivers' sign-in code and the password emails (not notifications, but texts in the same packs).</summary>
    private static readonly Dictionary<string, Dictionary<string, (string Subject, string Body)>> OtherMessages = new()
    {
        [LanguageCatalog.Messages.SignInCode] = new()
        {
            ["en"] = ("Sign-in code", "Yardsyncs: sign-in code {code}. Valid for {minutes} minutes. Do not share it."),
            ["ar"] = ("رمز الدخول", "Yardsyncs: رمز الدخول {code}. صالح لمدة {minutes} دقائق. لا تشاركه مع أحد."),
            ["ur"] = ("لاگ اِن کوڈ", "Yardsyncs: لاگ اِن کوڈ {code}۔ {minutes} منٹ کے لیے درست۔ کسی کو نہ بتائیں۔"),
            ["hi"] = ("लॉगिन कोड", "Yardsyncs: लॉगिन कोड {code}। {minutes} मिनट तक मान्य। किसी को न बताएं।"),
        },
        [LanguageCatalog.Messages.PasswordInvite] = new()
        {
            ["en"] = ("your account is ready",
                "Hello {name},\n\nAn account was created for you on Yardsyncs. Set your password here (the link works once, for {days} days):\n\n{link}\n\nIf you did not expect this, ignore this email."),
            ["ar"] = ("حسابك جاهز",
                "مرحباً {name}،\n\nتم إنشاء حساب لك على Yardsyncs. عيّن كلمة المرور من هنا (يعمل الرابط مرة واحدة لمدة {days} أيام):\n\n{link}\n\nإذا لم تكن تتوقع هذه الرسالة فتجاهلها."),
        },
        [LanguageCatalog.Messages.PasswordReset] = new()
        {
            ["en"] = ("set a new password",
                "Hello {name},\n\nSomeone asked to reset the password of your Yardsyncs account. Set a new password here (the link works once, for {minutes} minutes):\n\n{link}\n\nIf it wasn't you, ignore this email: your password stays as it is."),
            ["ar"] = ("تعيين كلمة مرور جديدة",
                "مرحباً {name}،\n\nطُلب تغيير كلمة مرور حسابك على Yardsyncs. عيّن كلمة مرور جديدة من هنا (يعمل الرابط مرة واحدة لمدة {minutes} دقيقة):\n\n{link}\n\nإذا لم تكن أنت صاحب الطلب فتجاهل الرسالة: تبقى كلمة المرور كما هي."),
        },
    };

    [GeneratedRegex(@"\{([a-zA-Z]+)\}")]
    private static partial Regex Placeholder();

    public static IReadOnlyCollection<string> TemplateCodes => Templates.Keys;

    public static IReadOnlyCollection<string> Languages => ["ar", "en", "ur", "hi"];

    public static bool IsKnown(string templateCode) => Templates.ContainsKey(templateCode);

    /// <summary>Every message a language pack can translate: the notifications, the sign-in code, the password emails.</summary>
    public static IReadOnlyCollection<string> MessageCodes => [.. Templates.Keys, .. OtherMessages.Keys];

    /// <summary>The built-in English text of a message (for the translators' template).</summary>
    public static (string Subject, string Body) English(string message) =>
        (Templates.GetValueOrDefault(message) ?? OtherMessages[message])["en"];

    /// <summary>The {placeholders} each message may use: those of its English text.</summary>
    public static IReadOnlyDictionary<string, IReadOnlySet<string>> Placeholders =>
        Templates.Concat(OtherMessages).ToDictionary(
            t => t.Key,
            t => (IReadOnlySet<string>)Placeholder().Matches(t.Value["en"].Subject + " " + t.Value["en"].Body)
                .Select(m => m.Groups[1].Value).ToHashSet());

    /// <summary>Fills the template; a missing value becomes "-" so a message is never sent with raw braces.</summary>
    /// <param name="brand">The name the message is signed with (the terminal's or the installation's); default Yardsyncs.</param>
    public static RenderedMessage Render(string templateCode, string? language, IReadOnlyDictionary<string, string> values, string? brand = null)
    {
        var name = string.IsNullOrWhiteSpace(brand) ? Brand : brand.Trim();
        if (!Templates.TryGetValue(templateCode, out var byLanguage) && !OtherMessages.TryGetValue(templateCode, out byLanguage))
            throw new ArgumentException($"Unknown notification template '{templateCode}'.", nameof(templateCode));

        // The language's pack, its built-in text, then English (an English pack can reword English too).
        var lang = (language ?? "en").Trim().ToLowerInvariant();
        var builtIn = byLanguage.TryGetValue(lang, out var own) ? own : byLanguage["en"];
        var packed = LanguageCatalog.Message(lang, templateCode)
                     ?? (byLanguage.ContainsKey(lang) ? null : LanguageCatalog.Message("en", templateCode));
        var (subject, body) = packed is null ? builtIn : (packed.Subject ?? builtIn.Subject, packed.Body);

        // The reason (from the TOS or the terminal, in English) and the slot's day and month in the reader's language.
        string InLanguage(string key, string value) => key switch
        {
            Keys.Reason => ServerText.Translate(value, lang),
            Keys.Slot => ServerText.Dates(value, lang),
            _ => value,
        };
        string Fill(string s) => Placeholder().Replace(s, m =>
            values.TryGetValue(m.Groups[1].Value, out var v) && !string.IsNullOrWhiteSpace(v) ? InLanguage(m.Groups[1].Value, v.Trim()) : "-");

        return new RenderedMessage($"{name} — {Fill(subject)}", Fill(body.Replace(Brand, name, StringComparison.Ordinal)));
    }

    /// <summary>"Tue 20 Oct 10:00–11:00" in the terminal's time zone (invariant month/day names).</summary>
    public static string SlotLabel(DateTimeOffset start, DateTimeOffset end, TimeZoneInfo timeZone)
    {
        var s = TimeZoneInfo.ConvertTime(start, timeZone);
        var e = TimeZoneInfo.ConvertTime(end, timeZone);
        return $"{s.ToString("ddd dd MMM HH:mm", CultureInfo.InvariantCulture)}–{e.ToString("HH:mm", CultureInfo.InvariantCulture)}";
    }
}
