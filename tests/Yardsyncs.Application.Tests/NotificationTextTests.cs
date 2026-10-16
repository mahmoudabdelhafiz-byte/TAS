using Yardsyncs.Application.Notifications;
using Yardsyncs.Domain;

namespace Yardsyncs.Application.Tests;

public class NotificationTextTests
{
    private static readonly Dictionary<string, string> Values = new()
    {
        [NotificationText.Keys.AppointmentNo] = "DEMO-261020-7KQ4M",
        [NotificationText.Keys.Slot] = "Tue 20 Oct 10:00–11:00",
        [NotificationText.Keys.Reason] = "Customs hold on MSCU1234566",
        [NotificationText.Keys.GateOpens] = "09:30",
        [NotificationText.Keys.Items] = "MSCU1234566",
        [NotificationText.Keys.Stops] = "B2 (14.03.2) → E1",
        [NotificationText.Keys.Container] = "MSCU1234566",
        [NotificationText.Keys.Terminal] = "Demo Container Terminal",
    };

    public static TheoryData<string, string> AllTemplatesAndLanguages()
    {
        var data = new TheoryData<string, string>();
        foreach (var template in NotificationText.TemplateCodes)
            foreach (var language in NotificationText.Languages)
                data.Add(template, language);
        return data;
    }

    [Theory]
    [MemberData(nameof(AllTemplatesAndLanguages))]
    public void Every_template_exists_in_every_language_and_fills_its_placeholders(string template, string language)
    {
        var message = NotificationText.Render(template, language, Values);

        Assert.DoesNotContain("{", message.Body);
        Assert.DoesNotContain("{", message.Subject);
        // Messages about a visit name the appointment; messages about a container name the container.
        Assert.Contains(template == NotificationTemplates.ContainerReady ? "MSCU1234566" : "DEMO-261020-7KQ4M", message.Body);
        Assert.StartsWith("Yardsyncs", message.Subject);
        Assert.Contains("Yardsyncs", message.Body);
        Assert.True(message.Body.Length <= 300, $"{template}/{language} is {message.Body.Length} characters");
    }

    [Fact]
    public void Templates_carry_the_right_details()
    {
        Assert.Contains("Customs hold on MSCU1234566", NotificationText.Render(NotificationTemplates.StatusRed, "en", Values).Body);
        Assert.Contains("09:30", NotificationText.Render(NotificationTemplates.SlotReminder, "ar", Values).Body);
        Assert.Contains("الموعد", NotificationText.Render(NotificationTemplates.StatusCleared, "ar", Values).Body);
        Assert.Contains("Tue 20 Oct 10:00–11:00", NotificationText.Render(NotificationTemplates.SlotClosed, "hi", Values).Body);
    }

    [Fact]
    public void Unknown_language_falls_back_to_english_and_missing_values_become_a_dash()
    {
        var message = NotificationText.Render(NotificationTemplates.StatusRed, "fr", new Dictionary<string, string>
        {
            [NotificationText.Keys.AppointmentNo] = "X-1",
        });

        Assert.StartsWith("Yardsyncs: appointment X-1 (-) is on hold: -.", message.Body);
        Assert.Throws<ArgumentException>(() => NotificationText.Render("NOPE", "en", Values));
    }

    [Fact]
    public void Slot_label_is_local_and_culture_independent()
    {
        var riyadh = TimeZoneInfo.FindSystemTimeZoneById("Asia/Riyadh");
        var start = new DateTimeOffset(2026, 10, 20, 7, 0, 0, TimeSpan.Zero);

        Assert.Equal("Tue 20 Oct 10:00–11:00", NotificationText.SlotLabel(start, start.AddHours(1), riyadh));
    }
}
