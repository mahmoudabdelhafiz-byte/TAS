using Tas.Application.Notifications;
using Tas.Domain;
using Tas.Domain.Booking;

namespace Tas.Application.Tests;

public class NotificationRendererTests
{
    private static readonly NotificationData Data = new(
        "DEMO-261020-0001",
        "Demo Terminal",
        new DateTime(2026, 10, 20, 10, 0, 0),
        new DateTime(2026, 10, 20, 11, 0, 0),
        "1234 ABJ",
        [new NotificationProblem("MSCU1234566", CheckType.CustomsRelease)]);

    public static TheoryData<string, string> AllTemplatesAndLanguages()
    {
        var data = new TheoryData<string, string>();
        foreach (var template in NotificationTemplates.All)
        foreach (var language in NotificationRenderer.Languages)
            data.Add(template, language);
        return data;
    }

    [Theory]
    [MemberData(nameof(AllTemplatesAndLanguages))]
    public void Every_template_renders_in_every_language_for_sms_and_email(string template, string language)
    {
        var sms = NotificationRenderer.Render(template, language, NotificationChannel.Sms, Data);
        Assert.NotNull(sms);
        Assert.Null(sms.Subject);
        Assert.Equal(language, sms.Language);
        Assert.Contains("DEMO-261020-0001", sms.Body);
        Assert.Contains("Demo Terminal", sms.Body);
        Assert.Contains("20/10 10:00-11:00", sms.Body);
        Assert.DoesNotContain("{", sms.Body);
        Assert.True(sms.Body.Length <= 280, $"SMS too long ({sms.Body.Length}): {sms.Body}");

        var email = NotificationRenderer.Render(template, language, NotificationChannel.Email, Data);
        Assert.NotNull(email);
        Assert.False(string.IsNullOrWhiteSpace(email.Subject));
        Assert.DoesNotContain("{", email.Subject);
        Assert.DoesNotContain("{", email.Body);
        Assert.StartsWith(sms.Body, email.Body);
        Assert.Contains("1234 ABJ", email.Body);
        Assert.Equal(language is "ar" or "ur", email.RightToLeft);
    }

    [Fact]
    public void Red_status_names_the_container_and_the_problem_in_the_recipients_language()
    {
        var ar = NotificationRenderer.Render(NotificationTemplates.StatusRed, "ar", NotificationChannel.Sms, Data)!;
        Assert.Contains("لا تتحرك", ar.Body);
        Assert.Contains("MSCU1234566 (حجز جمركي)", ar.Body);

        var en = NotificationRenderer.Render(NotificationTemplates.StatusRed, "en", NotificationChannel.Sms, Data)!;
        Assert.Contains("Do NOT set off", en.Body);
        Assert.Contains("MSCU1234566 (customs hold)", en.Body);

        var ur = NotificationRenderer.Render(NotificationTemplates.StatusRed, "ur", NotificationChannel.Sms, Data)!;
        Assert.Contains("MSCU1234566 (کسٹمز ہولڈ)", ur.Body);
    }

    [Fact]
    public void Several_problems_are_listed_once_each()
    {
        var data = Data with
        {
            Problems =
            [
                new("MSCU1234566", CheckType.CustomsRelease),
                new("MSCU1234566", CheckType.CustomsRelease),
                new("OOLU6200024", CheckType.Payment),
            ],
        };
        var body = NotificationRenderer.Render(NotificationTemplates.StatusRed, "en", NotificationChannel.Sms, data)!.Body;
        Assert.Contains("MSCU1234566 (customs hold), OOLU6200024 (charges unpaid)", body);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(body, "MSCU1234566"));
    }

    [Fact]
    public void Without_problems_a_generic_reason_is_used_and_without_a_truck_the_phrase_is_left_out()
    {
        var data = Data with { Problems = [], TruckPlate = null };
        Assert.Contains("a check failed", NotificationRenderer.Render(NotificationTemplates.StatusRed, "en", NotificationChannel.Sms, data)!.Body);

        var reminder = NotificationRenderer.Render(NotificationTemplates.SlotReminder, "en", NotificationChannel.Sms, data)!.Body;
        Assert.DoesNotContain("truck", reminder);
        Assert.Contains("appointment DEMO-261020-0001, 20/10 10:00-11:00.", reminder);
    }

    [Theory]
    [InlineData("AR", "ar")]
    [InlineData("ar-SA", "ar")]
    [InlineData(" ur ", "ur")]
    [InlineData("fr", "en")]
    [InlineData(null, "en")]
    [InlineData("", "en")]
    public void Languages_are_normalized_with_english_as_fallback(string? input, string expected)
    {
        Assert.Equal(expected, NotificationRenderer.NormalizeLanguage(input));
        Assert.Equal(expected, NotificationRenderer.Render(NotificationTemplates.StatusCleared, input, NotificationChannel.Sms, Data)!.Language);
    }

    [Fact]
    public void Unknown_templates_are_not_rendered()
    {
        Assert.Null(NotificationRenderer.Render("NO_SUCH_TEMPLATE", "en", NotificationChannel.Sms, Data));
        Assert.False(NotificationRenderer.IsKnownTemplate("NO_SUCH_TEMPLATE"));
        Assert.All(NotificationTemplates.All, t => Assert.True(NotificationRenderer.IsKnownTemplate(t)));
    }

    [Fact]
    public void Every_failing_check_type_has_a_name_in_every_language()
    {
        foreach (var check in Enum.GetValues<CheckType>())
        foreach (var language in NotificationRenderer.Languages)
        {
            var body = NotificationRenderer.Render(NotificationTemplates.StatusRed, language, NotificationChannel.Sms,
                Data with { Problems = [new("X1", check)] })!.Body;
            Assert.DoesNotContain($"({check})", body); // not the raw enum name
        }
    }
}

public class NotificationRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 7, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset SlotEnd = Now.AddHours(3);
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(12);

    private static string? Skip(
        string template, AppointmentStatus status = AppointmentStatus.Confirmed, bool failing = true,
        bool superseded = false, DateTimeOffset? slotEnd = null, DateTimeOffset? created = null) =>
        NotificationRelevance.SkipReason(template, status, slotEnd ?? SlotEnd, failing, superseded, created ?? Now.AddMinutes(-1), Now, MaxAge);

    [Fact]
    public void Live_appointments_get_their_messages()
    {
        Assert.Null(Skip(NotificationTemplates.StatusRed, failing: true));
        Assert.Null(Skip(NotificationTemplates.StatusCleared, failing: false));
        Assert.Null(Skip(NotificationTemplates.SlotReminder, AppointmentStatus.EnRoute));
        Assert.Null(Skip(NotificationTemplates.SlotClosed));
    }

    [Theory]
    [InlineData(AppointmentStatus.Cancelled)]
    [InlineData(AppointmentStatus.Completed)]
    [InlineData(AppointmentStatus.GateIn)]
    [InlineData(AppointmentStatus.NoShow)]
    public void Messages_for_finished_appointments_are_skipped(AppointmentStatus status)
    {
        foreach (var template in NotificationTemplates.All)
            Assert.Contains(status.ToString(), Skip(template, status, failing: template == NotificationTemplates.StatusRed));
    }

    [Fact]
    public void Stale_superseded_or_contradicted_messages_are_skipped()
    {
        Assert.Contains("passed", Skip(NotificationTemplates.SlotReminder, slotEnd: Now));
        Assert.Contains("old", Skip(NotificationTemplates.SlotClosed, created: Now.AddHours(-13)));
        Assert.Contains("Superseded", Skip(NotificationTemplates.StatusRed, superseded: true));
        Assert.Contains("Superseded", Skip(NotificationTemplates.StatusCleared, failing: false, superseded: true));
        Assert.Contains("Nothing is failing", Skip(NotificationTemplates.StatusRed, failing: false));
        Assert.Contains("failing again", Skip(NotificationTemplates.StatusCleared, failing: true));
        Assert.Contains("arrived", Skip(NotificationTemplates.SlotReminder, AppointmentStatus.Arrived));
        Assert.Null(Skip(NotificationTemplates.SlotReminder, superseded: true)); // superseding only applies to status messages
    }

    [Theory]
    [InlineData("+966500000001", PhoneNumberFormat.E164, "+966500000001")]
    [InlineData("+966500000001", PhoneNumberFormat.NoPlus, "966500000001")]
    [InlineData("+966 50 000 0001", PhoneNumberFormat.International00, "00966500000001")]
    [InlineData("0500000001", PhoneNumberFormat.NoPlus, null)]
    [InlineData("", PhoneNumberFormat.E164, null)]
    [InlineData(null, PhoneNumberFormat.E164, null)]
    public void Mobile_numbers_are_formatted_for_the_gateway(string? input, PhoneNumberFormat format, string? expected) =>
        Assert.Equal(expected, PhoneNumbers.Format(input, format));

    [Fact]
    public void Retries_back_off_and_stop()
    {
        Assert.Equal(TimeSpan.FromMinutes(1), NotificationRetry.DelayAfter(1));
        Assert.Equal(TimeSpan.FromMinutes(5), NotificationRetry.DelayAfter(2));
        Assert.Equal(TimeSpan.FromMinutes(30), NotificationRetry.DelayAfter(9));
        Assert.False(NotificationRetry.Exhausted(4, 5));
        Assert.True(NotificationRetry.Exhausted(5, 5));
    }
}
