using System.Globalization;
using Tas.Domain;
using Tas.Domain.Booking;

namespace Tas.Application.Notifications;

/// <summary>
/// Renders notification templates in Arabic, English, Urdu and Hindi. SMS texts are kept short (an Arabic
/// SMS segment is 70 characters); emails add a details block. Unknown languages fall back to English.
///
/// Placeholders: {terminal} {no} {slot} {truck} {problems}. Times use Latin digits (dd/MM HH:mm) in every
/// language so that numbers read the same on the driver's phone and the gate screen.
/// Urdu and Hindi texts should be reviewed by a native speaker before go-live.
/// </summary>
public static class NotificationRenderer
{
    public static readonly IReadOnlyList<string> Languages = ["ar", "en", "ur", "hi"];

    private sealed record Texts(string Subject, string Sms, string Action);

    private sealed record Labels(string Appointment, string Time, string Truck, string Terminal, string Problems, string GenericProblem, string ListSeparator, string TruckPhrase);

    private static readonly Dictionary<string, Labels> LabelsByLanguage = new()
    {
        ["en"] = new("Appointment", "Time", "Truck", "Terminal", "Problems", "a check failed", ", ", ", truck {truck}"),
        ["ar"] = new("الموعد", "الوقت", "الشاحنة", "المحطة", "المشكلات", "فشل أحد الفحوصات", "، ", "، الشاحنة {truck}"),
        ["ur"] = new("اپوائنٹمنٹ", "وقت", "ٹرک", "ٹرمینل", "مسائل", "ایک چیک ناکام ہوا", "، ", "، ٹرک {truck}"),
        ["hi"] = new("अपॉइंटमेंट", "समय", "ट्रक", "टर्मिनल", "समस्याएँ", "एक जाँच विफल हुई", ", ", ", ट्रक {truck}"),
    };

    private static readonly Dictionary<(string Template, string Language), Texts> TextsByKey = new()
    {
        // ---- STATUS_RED: a hold or similar appeared after booking.
        [(NotificationTemplates.StatusRed, "en")] = new(
            "Do not set off: appointment {no} is not cleared",
            "{terminal}: Do NOT set off for appointment {no} ({slot}): {problems}. Wait for the all-clear message.",
            "The appointment is kept. Resolve the problem with the line, customs or the terminal, or move the appointment to a later slot."),
        [(NotificationTemplates.StatusRed, "ar")] = new(
            "لا تتحرك: الموعد {no} غير مُصرَّح",
            "{terminal}: لا تتحرك لموعد {no} ({slot}): {problems}. انتظر رسالة السماح بالتحرك.",
            "الموعد ما زال محجوزًا. عالج المشكلة مع الخط الملاحي أو الجمارك أو المحطة، أو انقل الموعد إلى وقت لاحق."),
        [(NotificationTemplates.StatusRed, "ur")] = new(
            "روانہ نہ ہوں: اپوائنٹمنٹ {no} کلیئر نہیں",
            "{terminal}: اپوائنٹمنٹ {no} ({slot}) کے لیے روانہ نہ ہوں: {problems}۔ کلیئرنس کے پیغام کا انتظار کریں۔",
            "اپوائنٹمنٹ برقرار ہے۔ مسئلہ شپنگ لائن، کسٹمز یا ٹرمینل کے ساتھ حل کریں، یا اپوائنٹمنٹ بعد کے وقت پر منتقل کریں۔"),
        [(NotificationTemplates.StatusRed, "hi")] = new(
            "रवाना न हों: अपॉइंटमेंट {no} क्लियर नहीं है",
            "{terminal}: अपॉइंटमेंट {no} ({slot}) के लिए रवाना न हों: {problems}। क्लियरेंस संदेश का इंतज़ार करें।",
            "अपॉइंटमेंट बना हुआ है। समस्या शिपिंग लाइन, कस्टम या टर्मिनल के साथ हल करें, या अपॉइंटमेंट बाद के समय पर ले जाएँ।"),

        // ---- STATUS_CLEARED: everything that failed is clear again.
        [(NotificationTemplates.StatusCleared, "en")] = new(
            "Cleared: appointment {no} can go ahead",
            "{terminal}: Appointment {no} ({slot}) is cleared. You can set off.",
            "All checks are clear again."),
        [(NotificationTemplates.StatusCleared, "ar")] = new(
            "تم التصريح: يمكن تنفيذ الموعد {no}",
            "{terminal}: تم التصريح للموعد {no} ({slot}). يمكنك التحرك.",
            "جميع الفحوصات سليمة الآن."),
        [(NotificationTemplates.StatusCleared, "ur")] = new(
            "کلیئر: اپوائنٹمنٹ {no} پر عمل ہو سکتا ہے",
            "{terminal}: اپوائنٹمنٹ {no} ({slot}) کلیئر ہے۔ آپ روانہ ہو سکتے ہیں۔",
            "تمام چیک دوبارہ کلیئر ہیں۔"),
        [(NotificationTemplates.StatusCleared, "hi")] = new(
            "क्लियर: अपॉइंटमेंट {no} आगे बढ़ सकता है",
            "{terminal}: अपॉइंटमेंट {no} ({slot}) क्लियर है। आप रवाना हो सकते हैं।",
            "सभी जाँचें फिर से क्लियर हैं।"),

        // ---- SLOT_REMINDER: a few hours before the slot.
        [(NotificationTemplates.SlotReminder, "en")] = new(
            "Reminder: appointment {no} at {slot}",
            "{terminal}: Reminder: appointment {no}, {slot}{truck}. Check the status in the app before setting off.",
            "If no truck or driver is assigned yet, assign them now so the gate can recognise the truck."),
        [(NotificationTemplates.SlotReminder, "ar")] = new(
            "تذكير: الموعد {no} في {slot}",
            "{terminal}: تذكير: الموعد {no}، {slot}{truck}. تحقق من الحالة في التطبيق قبل التحرك.",
            "إذا لم تُحدَّد الشاحنة أو السائق بعد، حدِّدهما الآن ليتعرّف عليها نظام البوابة."),
        [(NotificationTemplates.SlotReminder, "ur")] = new(
            "یاد دہانی: اپوائنٹمنٹ {no}، {slot}",
            "{terminal}: یاد دہانی: اپوائنٹمنٹ {no}، {slot}{truck}۔ روانگی سے پہلے ایپ میں اسٹیٹس چیک کریں۔",
            "اگر ابھی تک ٹرک یا ڈرائیور مقرر نہیں ہوا تو ابھی مقرر کریں تاکہ گیٹ ٹرک کو پہچان سکے۔"),
        [(NotificationTemplates.SlotReminder, "hi")] = new(
            "रिमाइंडर: अपॉइंटमेंट {no}, {slot}",
            "{terminal}: रिमाइंडर: अपॉइंटमेंट {no}, {slot}{truck}। रवाना होने से पहले ऐप में स्टेटस देखें।",
            "अगर अभी तक ट्रक या ड्राइवर तय नहीं है, तो अभी तय करें ताकि गेट ट्रक को पहचान सके।"),

        // ---- SLOT_CLOSED: the terminal closed the slot after it was booked.
        [(NotificationTemplates.SlotClosed, "en")] = new(
            "Slot closed: appointment {no} needs a new time",
            "{terminal}: The terminal closed the time of appointment {no} ({slot}). Do not set off until you get a new time.",
            "Please move the appointment to another slot."),
        [(NotificationTemplates.SlotClosed, "ar")] = new(
            "تم إغلاق الوقت: الموعد {no} يحتاج وقتًا جديدًا",
            "{terminal}: أغلقت المحطة وقت الموعد {no} ({slot}). لا تتحرك حتى يصلك وقت جديد.",
            "يرجى نقل الموعد إلى وقت آخر."),
        [(NotificationTemplates.SlotClosed, "ur")] = new(
            "وقت بند: اپوائنٹمنٹ {no} کو نیا وقت چاہیے",
            "{terminal}: ٹرمینل نے اپوائنٹمنٹ {no} ({slot}) کا وقت بند کر دیا ہے۔ نیا وقت ملنے تک روانہ نہ ہوں۔",
            "براہ کرم اپوائنٹمنٹ کو کسی اور وقت پر منتقل کریں۔"),
        [(NotificationTemplates.SlotClosed, "hi")] = new(
            "समय बंद: अपॉइंटमेंट {no} को नया समय चाहिए",
            "{terminal}: टर्मिनल ने अपॉइंटमेंट {no} ({slot}) का समय बंद कर दिया है। नया समय मिलने तक रवाना न हों।",
            "कृपया अपॉइंटमेंट को किसी दूसरे समय पर ले जाएँ।"),
    };

    private static readonly Dictionary<(CheckType Check, string Language), string> CheckNames = new()
    {
        [(CheckType.CustomsRelease, "en")] = "customs hold",
        [(CheckType.CustomsRelease, "ar")] = "حجز جمركي",
        [(CheckType.CustomsRelease, "ur")] = "کسٹمز ہولڈ",
        [(CheckType.CustomsRelease, "hi")] = "कस्टम होल्ड",
        [(CheckType.LineRelease, "en")] = "shipping line hold",
        [(CheckType.LineRelease, "ar")] = "حجز الخط الملاحي",
        [(CheckType.LineRelease, "ur")] = "شپنگ لائن ہولڈ",
        [(CheckType.LineRelease, "hi")] = "शिपिंग लाइन होल्ड",
        [(CheckType.TerminalHold, "en")] = "terminal hold",
        [(CheckType.TerminalHold, "ar")] = "حجز المحطة",
        [(CheckType.TerminalHold, "ur")] = "ٹرمینل ہولڈ",
        [(CheckType.TerminalHold, "hi")] = "टर्मिनल होल्ड",
        [(CheckType.Payment, "en")] = "charges unpaid",
        [(CheckType.Payment, "ar")] = "رسوم غير مدفوعة",
        [(CheckType.Payment, "ur")] = "واجبات ادا نہیں ہوئے",
        [(CheckType.Payment, "hi")] = "शुल्क बकाया",
        [(CheckType.Availability, "en")] = "not available",
        [(CheckType.Availability, "ar")] = "غير متاحة",
        [(CheckType.Availability, "ur")] = "دستیاب نہیں",
        [(CheckType.Availability, "hi")] = "उपलब्ध नहीं",
        [(CheckType.BookingValid, "en")] = "booking not valid",
        [(CheckType.BookingValid, "ar")] = "الحجز غير صالح",
        [(CheckType.BookingValid, "ur")] = "بکنگ درست نہیں",
        [(CheckType.BookingValid, "hi")] = "बुकिंग मान्य नहीं",
        [(CheckType.Cutoff, "en")] = "cut-off passed",
        [(CheckType.Cutoff, "ar")] = "انتهى الموعد النهائي",
        [(CheckType.Cutoff, "ur")] = "کٹ آف گزر گیا",
        [(CheckType.Cutoff, "hi")] = "कट-ऑफ़ निकल गया",
        [(CheckType.Vgm, "en")] = "VGM missing",
        [(CheckType.Vgm, "ar")] = "الوزن الإجمالي (VGM) غير مسجل",
        [(CheckType.Vgm, "ur")] = "VGM موجود نہیں",
        [(CheckType.Vgm, "hi")] = "VGM उपलब्ध नहीं",
        [(CheckType.CargoRelease, "en")] = "cargo not released",
        [(CheckType.CargoRelease, "ar")] = "البضاعة غير مفرج عنها",
        [(CheckType.CargoRelease, "ur")] = "کارگو ریلیز نہیں ہوا",
        [(CheckType.CargoRelease, "hi")] = "कार्गो रिलीज़ नहीं हुआ",
        [(CheckType.ContainerNumber, "en")] = "container number invalid",
        [(CheckType.ContainerNumber, "ar")] = "رقم الحاوية غير صحيح",
        [(CheckType.ContainerNumber, "ur")] = "کنٹینر نمبر غلط ہے",
        [(CheckType.ContainerNumber, "hi")] = "कंटेनर नंबर गलत है",
        [(CheckType.TosUnavailable, "en")] = "not confirmed yet",
        [(CheckType.TosUnavailable, "ar")] = "لم يتم التأكيد بعد",
        [(CheckType.TosUnavailable, "ur")] = "ابھی تصدیق نہیں ہوئی",
        [(CheckType.TosUnavailable, "hi")] = "अभी पुष्टि नहीं हुई",
    };

    public static bool IsKnownTemplate(string template) => TextsByKey.ContainsKey((template, "en"));

    /// <summary>"ar", "AR ", "ar-SA" → "ar"; anything unsupported → "en".</summary>
    public static string NormalizeLanguage(string? language)
    {
        var code = (language ?? "").Trim().ToLowerInvariant();
        if (code.Length > 2)
            code = code[..2];
        return Languages.Contains(code) ? code : "en";
    }

    /// <summary>Renders a template; null if the template is unknown.</summary>
    public static RenderedMessage? Render(string template, string? language, NotificationChannel channel, NotificationData data)
    {
        var lang = NormalizeLanguage(language);
        if (!TextsByKey.TryGetValue((template, lang), out var texts))
            return null;
        var labels = LabelsByLanguage[lang];

        var slot = string.Create(CultureInfo.InvariantCulture, $"{data.SlotStartLocal:dd/MM HH:mm}-{data.SlotEndLocal:HH:mm}");
        var problems = data.Problems.Count == 0
            ? labels.GenericProblem
            : string.Join(labels.ListSeparator, data.Problems
                .DistinctBy(p => (p.Item, p.Check))
                .Select(p => $"{p.Item} ({CheckName(p.Check, lang)})"));
        var truck = string.IsNullOrWhiteSpace(data.TruckPlate) ? "" : labels.TruckPhrase.Replace("{truck}", data.TruckPlate);

        string Fill(string text) => text
            .Replace("{terminal}", data.TerminalName)
            .Replace("{no}", data.AppointmentNo)
            .Replace("{slot}", slot)
            .Replace("{truck}", truck)
            .Replace("{problems}", problems);

        var rtl = lang is "ar" or "ur";
        var sms = Fill(texts.Sms);
        if (channel != NotificationChannel.Email)
            return new RenderedMessage(null, sms, lang, rtl);

        var lines = new List<string>
        {
            sms,
            "",
            texts.Action,
            "",
            $"{labels.Appointment}: {data.AppointmentNo}",
            $"{labels.Terminal}: {data.TerminalName}",
            $"{labels.Time}: {slot}",
        };
        if (!string.IsNullOrWhiteSpace(data.TruckPlate))
            lines.Add($"{labels.Truck}: {data.TruckPlate}");
        if (data.Problems.Count > 0)
            lines.Add($"{labels.Problems}: {problems}");

        return new RenderedMessage(Fill(texts.Subject), string.Join("\n", lines), lang, rtl);
    }

    private static string CheckName(CheckType check, string lang) =>
        CheckNames.TryGetValue((check, lang), out var name) ? name : CheckNames.GetValueOrDefault((check, "en"), check.ToString());
}
