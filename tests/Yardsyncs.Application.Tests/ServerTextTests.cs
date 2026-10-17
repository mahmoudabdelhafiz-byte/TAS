using Yardsyncs.Application.Languages;

namespace Yardsyncs.Application.Tests;

public class ServerTextTests
{
    [Theory]
    [InlineData("Customs hold on MSCU1234566", "حجز جمركي على MSCU1234566")]
    [InlineData("Item 2: Charges for EDO-77 are unpaid", "البند 2: رسوم EDO-77 غير مدفوعة")]
    [InlineData("Customs hold", "حجز جمركي")]
    [InlineData("Late: the arrival window closed at 11:00.", "متأخر: أُغلقت نافذة الوصول الساعة 11:00.")]
    [InlineData("Admit — note: Customs hold", "سماح بالدخول — ملاحظة: حجز جمركي")]
    [InlineData("Admit", "سماح بالدخول")]
    [InlineData("Ready — proceed to the gate", "جاهزة — توجّه إلى البوابة")]
    [InlineData("Appointment Tue 20 Oct 10:00 — gate opens for you at 09:30", "الموعد الثلاثاء 20 أكتوبر 10:00 — تفتح لك البوابة الساعة 09:30")]
    [InlineData("That code isn't right. Check the phone's time and type the code the app shows now.", "الرمز غير صحيح. تحقق من وقت الهاتف واكتب الرمز الذي يظهره التطبيق الآن.")]
    [InlineData("This truck already has 2 appointment(s) that day (limit 2).", "لهذه الشاحنة 2 موعد في ذلك اليوم (الحد 2).")]
    public void Arabic_readers_get_the_servers_sentences_in_arabic(string english, string arabic) =>
        Assert.Equal(arabic, ServerText.Translate(english, "ar"));

    [Fact]
    public void Unknown_sentences_and_other_languages_stay_as_they_are()
    {
        Assert.Equal("Something the catalog doesn't know.", ServerText.Translate("Something the catalog doesn't know.", "ar"));
        Assert.Equal("Customs hold on MSCU1234566", ServerText.Translate("Customs hold on MSCU1234566", "en"));
        Assert.Equal("Customs hold on MSCU1234566", ServerText.Translate("Customs hold on MSCU1234566", "fr"));
        Assert.True(ServerText.Count("ar") > 80);
    }

    [Theory]
    [InlineData("ar", "ar")]
    [InlineData("ar-SA,ar;q=0.9,en;q=0.8", "ar")]
    [InlineData("en-US,en;q=0.9", "en")]
    [InlineData("fr-FR,ar;q=0.5", "ar")]
    [InlineData(null, "en")]
    [InlineData("", "en")]
    public void The_language_comes_from_accept_language(string? header, string language) =>
        Assert.Equal(language, ServerText.FromAcceptLanguage(header));

    [Fact]
    public void The_current_request_language_is_used()
    {
        ServerText.Language = "ar";
        try
        {
            Assert.Equal("اكتملت الزيارة", ServerText.T("Visit completed"));
            Assert.Equal(new[] { "سماح بالدخول" }, ServerText.T(new[] { "Admit" }));
            Assert.Null(ServerText.TOrNull(null));
        }
        finally
        {
            ServerText.Language = "en";
        }
        Assert.Equal("Visit completed", ServerText.T("Visit completed"));
    }
}
