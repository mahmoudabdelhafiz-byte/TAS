using Tas.Domain;

namespace Tas.Application.Notifications;

/// <param name="Link">The one-time link to set the password.</param>
/// <param name="ValidFor">How long the link works, e.g. 60 minutes or 7 days.</param>
public sealed record AccountMessageData(string FullName, string Email, string Link, TimeSpan ValidFor, string? CompanyOrTerminal);

/// <summary>Password reset and invitation emails in Arabic, English, Urdu and Hindi.</summary>
public static class AccountMessageRenderer
{
    private sealed record Texts(string Subject, string Body);

    // {name} {email} {link} {valid} {org}
    private static readonly Dictionary<(string Template, string Language), Texts> TextsByKey = new()
    {
        [(NotificationTemplates.PasswordReset, "en")] = new(
            "Reset your truck appointment password",
            "Hello {name},\n\nWe received a request to reset the password of {email}.\nSet a new password here (the link works once, for {valid}):\n\n{link}\n\nIf you did not ask for this, ignore this email; your password stays the same."),
        [(NotificationTemplates.PasswordReset, "ar")] = new(
            "إعادة تعيين كلمة المرور لنظام مواعيد الشاحنات",
            "مرحبًا {name}،\n\nتلقينا طلبًا لإعادة تعيين كلمة المرور للحساب {email}.\nعيّن كلمة مرور جديدة من هنا (الرابط صالح لمرة واحدة، لمدة {valid}):\n\n{link}\n\nإذا لم تطلب ذلك، تجاهل هذه الرسالة؛ ستبقى كلمة المرور كما هي."),
        [(NotificationTemplates.PasswordReset, "ur")] = new(
            "ٹرک اپوائنٹمنٹ کا پاس ورڈ ری سیٹ کریں",
            "السلام علیکم {name}،\n\nہمیں {email} کا پاس ورڈ ری سیٹ کرنے کی درخواست ملی ہے۔\nیہاں نیا پاس ورڈ سیٹ کریں (لنک ایک بار، {valid} کے لیے کام کرے گا):\n\n{link}\n\nاگر آپ نے یہ درخواست نہیں کی تو اس ای میل کو نظرانداز کریں؛ آپ کا پاس ورڈ وہی رہے گا۔"),
        [(NotificationTemplates.PasswordReset, "hi")] = new(
            "ट्रक अपॉइंटमेंट पासवर्ड रीसेट करें",
            "नमस्ते {name},\n\nहमें {email} का पासवर्ड रीसेट करने का अनुरोध मिला है।\nयहाँ नया पासवर्ड सेट करें (लिंक एक बार, {valid} तक काम करेगा):\n\n{link}\n\nअगर आपने यह अनुरोध नहीं किया, तो इस ईमेल को अनदेखा करें; आपका पासवर्ड वही रहेगा।"),

        [(NotificationTemplates.AccountInvite, "en")] = new(
            "Your truck appointment account{org}",
            "Hello {name},\n\nAn account has been created for you{org} with the email {email}.\nSet your password here to start (the link works once, for {valid}):\n\n{link}"),
        [(NotificationTemplates.AccountInvite, "ar")] = new(
            "حسابك في نظام مواعيد الشاحنات{org}",
            "مرحبًا {name}،\n\nتم إنشاء حساب لك{org} بالبريد {email}.\nعيّن كلمة المرور من هنا للبدء (الرابط صالح لمرة واحدة، لمدة {valid}):\n\n{link}"),
        [(NotificationTemplates.AccountInvite, "ur")] = new(
            "ٹرک اپوائنٹمنٹ میں آپ کا اکاؤنٹ{org}",
            "السلام علیکم {name}،\n\nآپ کے لیے{org} ای میل {email} کے ساتھ اکاؤنٹ بنایا گیا ہے۔\nشروع کرنے کے لیے یہاں پاس ورڈ سیٹ کریں (لنک ایک بار، {valid} کے لیے کام کرے گا):\n\n{link}"),
        [(NotificationTemplates.AccountInvite, "hi")] = new(
            "ट्रक अपॉइंटमेंट में आपका खाता{org}",
            "नमस्ते {name},\n\nआपके लिए{org} ईमेल {email} से खाता बनाया गया है।\nशुरू करने के लिए यहाँ पासवर्ड सेट करें (लिंक एक बार, {valid} तक काम करेगा):\n\n{link}"),
    };

    private static readonly Dictionary<string, (string Minutes, string Hours, string Days, string OrgPrefix)> Units = new()
    {
        ["en"] = ("{n} minutes", "{n} hours", "{n} days", " — {org}"),
        ["ar"] = ("{n} دقيقة", "{n} ساعة", "{n} أيام", " — {org}"),
        ["ur"] = ("{n} منٹ", "{n} گھنٹے", "{n} دن", " — {org}"),
        ["hi"] = ("{n} मिनट", "{n} घंटे", "{n} दिन", " — {org}"),
    };

    /// <summary>Renders an email; null for an unknown template.</summary>
    public static RenderedMessage? Render(string template, string? language, AccountMessageData data)
    {
        var lang = NotificationRenderer.NormalizeLanguage(language);
        if (!TextsByKey.TryGetValue((template, lang), out var texts))
            return null;

        var units = Units[lang];
        var valid = data.ValidFor.TotalDays >= 1 && data.ValidFor.TotalDays % 1 == 0
            ? units.Days.Replace("{n}", ((int)data.ValidFor.TotalDays).ToString())
            : data.ValidFor.TotalHours >= 2 && data.ValidFor.TotalHours % 1 == 0
                ? units.Hours.Replace("{n}", ((int)data.ValidFor.TotalHours).ToString())
                : units.Minutes.Replace("{n}", ((int)Math.Ceiling(data.ValidFor.TotalMinutes)).ToString());
        var org = string.IsNullOrWhiteSpace(data.CompanyOrTerminal) ? "" : units.OrgPrefix.Replace("{org}", data.CompanyOrTerminal);

        string Fill(string text) => text
            .Replace("{name}", data.FullName)
            .Replace("{email}", data.Email)
            .Replace("{link}", data.Link)
            .Replace("{valid}", valid)
            .Replace("{org}", org);

        return new RenderedMessage(Fill(texts.Subject), Fill(texts.Body), lang, lang is "ar" or "ur");
    }
}
