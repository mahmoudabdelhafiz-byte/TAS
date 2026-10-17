using Yardsyncs.Application.Safety;

namespace Yardsyncs.Application.Tests;

public class SafetyRulesTests
{
    private static readonly DateOnly Day = new(2026, 10, 18);

    private static Dictionary<string, LocalizedText> Texts(params (string Lang, string Title, string Body)[] texts) =>
        texts.ToDictionary(t => t.Lang, t => new LocalizedText(t.Title, t.Body));

    [Fact]
    public void A_campaign_needs_english_https_pictures_and_sensible_dates()
    {
        var ok = Texts(("en", "Wear your vest", "Outside the cab, always wear the high-visibility vest."), ("ar", "ارتدِ السترة", "خارج المقصورة ارتدِ السترة العاكسة دائماً."));
        Assert.Null(SafetyRules.CheckCampaign(new CampaignInput(ok, "https://example.com/vest.png", Day, Day.AddDays(30))));
        Assert.NotNull(SafetyRules.CheckCampaign(new CampaignInput(Texts(("ar", "ارتدِ السترة", "نص")), null, Day, Day)));
        Assert.NotNull(SafetyRules.CheckCampaign(new CampaignInput(ok, "http://example.com/vest.png", Day, Day)));
        Assert.NotNull(SafetyRules.CheckCampaign(new CampaignInput(ok, null, Day, Day.AddDays(-1))));
        Assert.NotNull(SafetyRules.CheckCampaign(new CampaignInput(ok, null, Day, Day.AddDays(400))));
        Assert.NotNull(SafetyRules.CheckCampaign(new CampaignInput(Texts(("en", "T", "B"), ("fr", "T", "B")), null, Day, Day)));
        Assert.NotNull(SafetyRules.CheckCampaign(new CampaignInput(Texts(("en", "T", "B"), ("ur", "T", "")), null, Day, Day)));
    }

    private static InductionQuestion Question(int correct, params string[] options) =>
        new(new Dictionary<string, QuestionText> { ["en"] = new("Where do you wait while the crane works?", options) }, correct);

    [Fact]
    public void An_induction_has_pages_questions_and_a_right_answer_each()
    {
        var page = new InductionPage(Texts(("en", "Speed", "20 km/h everywhere in the yard.")));
        Assert.Null(SafetyRules.CheckInduction(new InductionInput([page], [Question(1, "On the quay", "In the cab")], 12)));
        Assert.NotNull(SafetyRules.CheckInduction(new InductionInput([], [Question(1, "A", "B")])));
        Assert.NotNull(SafetyRules.CheckInduction(new InductionInput([page], [Question(2, "A", "B")])));
        Assert.NotNull(SafetyRules.CheckInduction(new InductionInput([page], [Question(0, "A")])));
        Assert.NotNull(SafetyRules.CheckInduction(new InductionInput([page], [Question(0, "A", "B")], 0)));

        // Another language must have the same answers as English.
        var mismatch = new InductionQuestion(new Dictionary<string, QuestionText>
        {
            ["en"] = new("Q", ["A", "B"]),
            ["ar"] = new("س", ["أ"]),
        }, 0);
        Assert.NotNull(SafetyRules.CheckInduction(new InductionInput([page], [mismatch])));
    }

    [Fact]
    public void Answers_are_marked_and_texts_fall_back_to_english()
    {
        var questions = new[] { Question(1, "A", "B"), Question(0, "A", "B"), Question(2, "A", "B", "C") };
        Assert.Empty(SafetyRules.Wrong(questions, [1, 0, 2]));
        Assert.Equal(new[] { 1, 2 }, SafetyRules.Wrong(questions, [1, 1]));
        Assert.Equal(new[] { 0, 1, 2 }, SafetyRules.Wrong(questions, null));

        var texts = Texts(("en", "Vest", "Wear it"), ("ar", "السترة", "ارتدها"));
        Assert.Equal("السترة", SafetyRules.Pick(texts, "ar").Title);
        Assert.Equal("Vest", SafetyRules.Pick(texts, "ur").Title);
    }
}
