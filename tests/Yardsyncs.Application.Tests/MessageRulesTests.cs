using Yardsyncs.Application.Messages;
using Yardsyncs.Domain.Booking;

namespace Yardsyncs.Application.Tests;

public class MessageRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.FromHours(3));

    [Fact]
    public void Every_ready_made_message_is_in_every_language_and_codes_are_unique()
    {
        Assert.Equal(MessageRules.Templates.Count, MessageRules.Templates.Select(t => t.Code).Distinct().Count());
        foreach (var template in MessageRules.Templates)
        {
            foreach (var language in new[] { "en", "ar", "ur", "hi" })
                Assert.False(string.IsNullOrWhiteSpace(template.Texts.GetValueOrDefault(language)), $"{template.Code}/{language}");
            Assert.True(template.Text("en").Length <= MessageRules.MaxLength);
        }
        Assert.Contains(MessageRules.Templates, t => t.Direction == MessageDirection.ToDriver);
        Assert.Contains(MessageRules.Templates, t => t.Direction == MessageDirection.FromDriver && t.Type == MessageType.CallRequest);
    }

    [Fact]
    public void A_ready_made_message_is_kept_in_english_and_read_in_the_readers_language()
    {
        var (error, type, code, body) = MessageRules.Check(MessageDirection.ToDriver, new MessageInput(TemplateCode: " go_to_trouble_desk "));
        Assert.Null(error);
        Assert.Equal((MessageType.Template, "GO_TO_TROUBLE_DESK", "Please come to the trouble desk at the gate."), (type, code, body));
        Assert.Equal("يرجى الحضور إلى مكتب المشكلات عند البوابة.", MessageRules.TextFor(code, body, "ar"));
        Assert.Equal(body, MessageRules.TextFor(code, body, "fr")); // no text in that language: English
        Assert.Equal("Free text", MessageRules.TextFor(null, "Free text", "ar"));
    }

    [Theory]
    [InlineData("GO_TO_TROUBLE_DESK", null)] // the control room's, not the driver's
    [InlineData("NO_SUCH", null)]
    [InlineData("RUNNING_LATE", "and a text")]
    [InlineData(null, "   ")]
    [InlineData(null, null)]
    public void A_driver_sends_their_own_ready_made_messages_or_a_text(string? code, string? text) =>
        Assert.NotNull(MessageRules.Check(MessageDirection.FromDriver, new MessageInput(code, text)).Error);

    [Fact]
    public void Texts_are_trimmed_and_limited()
    {
        Assert.Equal(((string?)null, MessageType.Text, (string?)null, "Gate 3?"), MessageRules.Check(MessageDirection.FromDriver, new MessageInput(Text: "  Gate 3? ")));
        Assert.NotNull(MessageRules.Check(MessageDirection.ToDriver, new MessageInput(Text: new string('a', MessageRules.MaxLength + 1))).Error);
    }

    [Fact]
    public void A_visit_is_open_while_live_and_for_a_day_after_its_slot()
    {
        Assert.True(MessageRules.IsOpen(AppointmentStatus.GateIn, Now.AddDays(-3), Now));
        Assert.True(MessageRules.IsOpen(AppointmentStatus.Completed, Now.AddHours(-20), Now));
        Assert.False(MessageRules.IsOpen(AppointmentStatus.Completed, Now.AddHours(-30), Now));
        Assert.False(MessageRules.IsOpen(AppointmentStatus.Draft, Now.AddHours(1), Now));
    }
}
