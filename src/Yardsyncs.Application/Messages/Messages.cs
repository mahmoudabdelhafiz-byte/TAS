using Yardsyncs.Domain.Booking;

namespace Yardsyncs.Application.Messages;

/// <summary>A ready-made message: who sends it and its text in each language (English when a language lacks it).</summary>
public sealed record ChatTemplate(string Code, MessageDirection Direction, MessageType Type, IReadOnlyDictionary<string, string> Texts)
{
    public string Text(string? language) =>
        Texts.TryGetValue((language ?? "en").Trim().ToLowerInvariant(), out var text) ? text : Texts["en"];
}

/// <summary>A message as the driver or the control room sees it, in the reader's language for ready-made ones.</summary>
public sealed record MessageView(
    long MessageId,
    MessageDirection Direction,
    MessageType Type,
    string? TemplateCode,
    string Text,
    string? SenderName,
    DateTimeOffset SentAt,
    DateTimeOffset? ReadAt);

/// <summary>A visit with messages, for the control room's list.</summary>
public sealed record ConversationView(
    string AppointmentNo,
    AppointmentStatus Status,
    DateTimeOffset SlotStart,
    string CompanyName,
    string? TruckPlate,
    string? DriverName,
    string? DriverMobile,
    int Unread,
    MessageView Last);

/// <summary>Either a ready-made message (its code) or a text.</summary>
public sealed record MessageInput(string? TemplateCode = null, string? Text = null);

/// <summary>A ready-made message to choose from, in the reader's language.</summary>
public sealed record MessageTemplateView(string Code, MessageType Type, string Text);

/// <summary>Ready-made messages, what may be sent and when a visit can still be written about.</summary>
public static class MessageRules
{
    public const int MaxLength = 500;

    /// <summary>A visit can be written about while live, and for a day after its slot (questions after leaving).</summary>
    public static readonly TimeSpan AfterSlot = TimeSpan.FromHours(24);

    private static ChatTemplate T(string code, MessageDirection direction, string en, string ar, string ur, string hi, MessageType type = MessageType.Template) =>
        new(code, direction, type, new Dictionary<string, string> { ["en"] = en, ["ar"] = ar, ["ur"] = ur, ["hi"] = hi });

    // Arabic, Urdu and Hindi to be reviewed by native speakers.
    public static readonly IReadOnlyList<ChatTemplate> Templates =
    [
        T("GO_TO_TROUBLE_DESK", MessageDirection.ToDriver,
            "Please come to the trouble desk at the gate.",
            "يرجى الحضور إلى مكتب المشكلات عند البوابة.",
            "براہ کرم گیٹ پر ٹربل ڈیسک پر آئیں۔",
            "कृपया गेट पर ट्रबल डेस्क पर आएँ।"),
        T("WAIT_IN_PARKING", MessageDirection.ToDriver,
            "Please wait in the truck parking. We will tell you when to come.",
            "يرجى الانتظار في موقف الشاحنات. سنخبرك متى تحضر.",
            "براہ کرم ٹرک پارکنگ میں انتظار کریں۔ ہم آپ کو بتائیں گے کب آنا ہے۔",
            "कृपया ट्रक पार्किंग में इंतज़ार करें। हम आपको बताएँगे कब आना है।"),
        T("PROCEED_TO_GATE", MessageDirection.ToDriver,
            "You can come to the gate now.",
            "يمكنك التوجه إلى البوابة الآن.",
            "آپ اب گیٹ پر آ سکتے ہیں۔",
            "आप अब गेट पर आ सकते हैं।"),
        T("DOCUMENTS_READY", MessageDirection.ToDriver,
            "Please have your ID and the delivery papers ready.",
            "يرجى تجهيز الهوية وأوراق التسليم.",
            "براہ کرم اپنی شناخت اور ڈیلیوری کے کاغذات تیار رکھیں۔",
            "कृपया अपनी पहचान और डिलीवरी के कागज़ तैयार रखें।"),
        T("CALL_CONTROL_ROOM", MessageDirection.ToDriver,
            "Please call the control room.",
            "يرجى الاتصال بغرفة التحكم.",
            "براہ کرم کنٹرول روم کو کال کریں۔",
            "कृपया कंट्रोल रूम को कॉल करें।"),

        T("RUNNING_LATE", MessageDirection.FromDriver,
            "I am running late.",
            "سأتأخر عن موعدي.",
            "مجھے دیر ہو رہی ہے۔",
            "मुझे देर हो रही है।"),
        T("AT_PARKING", MessageDirection.FromDriver,
            "I am waiting in the truck parking.",
            "أنا أنتظر في موقف الشاحنات.",
            "میں ٹرک پارکنگ میں انتظار کر رہا ہوں۔",
            "मैं ट्रक पार्किंग में इंतज़ार कर रहा हूँ।"),
        T("TRUCK_BREAKDOWN", MessageDirection.FromDriver,
            "My truck has broken down.",
            "تعطلت شاحنتي.",
            "میرا ٹرک خراب ہو گیا ہے۔",
            "मेरा ट्रक खराब हो गया है।"),
        T("CONTAINER_NOT_FOUND", MessageDirection.FromDriver,
            "I cannot find the container at the yard position.",
            "لا أجد الحاوية في موقعها بالساحة.",
            "مجھے یارڈ میں بتائی جگہ پر کنٹینر نہیں مل رہا۔",
            "मुझे यार्ड में बताई जगह पर कंटेनर नहीं मिल रहा।"),
        T("CALL_ME", MessageDirection.FromDriver,
            "Please call me.",
            "يرجى الاتصال بي.",
            "براہ کرم مجھے کال کریں۔",
            "कृपया मुझे कॉल करें।",
            MessageType.CallRequest),
    ];

    public static ChatTemplate? Template(string? code) =>
        string.IsNullOrWhiteSpace(code) ? null : Templates.FirstOrDefault(t => t.Code == code.Trim().ToUpperInvariant());

    public static IReadOnlyList<MessageTemplateView> TemplatesFor(MessageDirection direction, string? language) =>
        Templates.Where(t => t.Direction == direction).Select(t => new MessageTemplateView(t.Code, t.Type, t.Text(language))).ToList();

    /// <summary>What is stored for a message: its type, template code and body (English for a ready-made one); or why not.</summary>
    public static (string? Error, MessageType Type, string? TemplateCode, string Body) Check(MessageDirection direction, MessageInput input)
    {
        var text = input.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(input.TemplateCode))
        {
            if (!string.IsNullOrEmpty(text))
                return ("Send either a ready-made message or a text, not both.", default, null, "");
            var template = Template(input.TemplateCode);
            if (template is null || template.Direction != direction)
                return ($"'{input.TemplateCode}' is not a ready-made message {(direction == MessageDirection.ToDriver ? "for drivers" : "from drivers")}.", default, null, "");
            return (null, template.Type, template.Code, template.Text("en"));
        }

        if (string.IsNullOrEmpty(text))
            return ("Write a message or choose a ready-made one.", default, null, "");
        if (text.Length > MaxLength)
            return ($"A message is at most {MaxLength} characters.", default, null, "");
        return (null, MessageType.Text, null, text);
    }

    /// <summary>Whether a visit can still be written about.</summary>
    public static bool IsOpen(AppointmentStatus status, DateTimeOffset slotEnd, DateTimeOffset now) =>
        status is AppointmentStatus.Confirmed or AppointmentStatus.EnRoute or AppointmentStatus.Arrived or AppointmentStatus.GateIn
        || (status is not AppointmentStatus.Draft && slotEnd + AfterSlot >= now);

    /// <summary>The text shown to a reader: a ready-made message in their language, else what was written.</summary>
    public static string TextFor(string? templateCode, string? body, string? language) =>
        Template(templateCode)?.Text(language) ?? body ?? "";
}
