using System.Text.Json;

namespace Yardsyncs.Application.Safety;

/// <summary>A title and a text in one language.</summary>
public sealed record LocalizedText(string Title, string Body);

/// <summary>A question in one language with its answer options (same order in every language).</summary>
public sealed record QuestionText(string Question, IReadOnlyList<string> Options);

/// <param name="Texts">By language (en required; ar, ur, hi optional — English is shown when a language is missing).</param>
public sealed record CampaignInput(
    IReadOnlyDictionary<string, LocalizedText> Texts, string? ImageUrl, DateOnly StartsOn, DateOnly EndsOn, IReadOnlyList<int>? CompanyIds = null, bool RequireAck = true);

public sealed record CampaignView(
    int CampaignId, IReadOnlyDictionary<string, LocalizedText> Texts, string? ImageUrl, DateOnly StartsOn, DateOnly EndsOn,
    IReadOnlyList<int>? CompanyIds, bool RequireAck, DateTimeOffset CreatedAt, bool Archived, bool Active, int Confirmed);

/// <summary>A campaign as the driver sees it, in the app's language.</summary>
public sealed record DriverCampaignView(int CampaignId, string TerminalName, string Title, string Body, string? ImageUrl, bool RequireAck, bool Confirmed, DateOnly EndsOn);

public sealed record InductionPage(IReadOnlyDictionary<string, LocalizedText> Texts);

/// <param name="Correct">The right option (0-based).</param>
public sealed record InductionQuestion(IReadOnlyDictionary<string, QuestionText> Texts, int Correct);

public sealed record InductionInput(IReadOnlyList<InductionPage> Pages, IReadOnlyList<InductionQuestion> Questions, int ValidMonths = 12);

/// <param name="ValidPasses">Drivers whose pass is valid now.</param>
public sealed record InductionView(
    IReadOnlyList<InductionPage> Pages, IReadOnlyList<InductionQuestion> Questions, int ValidMonths, int Version, DateTimeOffset UpdatedAt, int ValidPasses);

/// <summary>The course as the driver takes it, in the app's language (no answers).</summary>
public sealed record DriverInductionView(
    int TerminalId, string TerminalName, int Version, IReadOnlyList<LocalizedText> Pages, IReadOnlyList<QuestionText> Questions,
    DateTimeOffset? ValidUntil, bool Needed);

public sealed record AnswersInput(IReadOnlyList<int>? Answers);

/// <param name="Wrong">The questions answered wrongly (0-based), to read again.</param>
public sealed record AnswerResult(bool Passed, IReadOnlyList<int> Wrong, DateTimeOffset? ValidUntil);

/// <summary>What a campaign and an induction need, texts by language, and marking the answers.</summary>
public static class SafetyRules
{
    public static readonly IReadOnlyList<string> Languages = ["en", "ar", "ur", "hi"];
    public const int MaxTitle = 120;
    public const int MaxBody = 2000;
    public const int MaxCampaignDays = 366;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string? CheckCampaign(CampaignInput input)
    {
        if (TextsProblem(input.Texts, t => (t.Title, t.Body)) is { } problem)
            return problem;
        if (input.ImageUrl is { Length: > 0 } url
            && (url.Length > 400 || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps))
            return "The picture must be an https:// address (at most 400 characters).";
        if (input.EndsOn < input.StartsOn)
            return "The campaign must end on or after the day it starts.";
        if (input.EndsOn.DayNumber - input.StartsOn.DayNumber + 1 > MaxCampaignDays)
            return $"A campaign runs at most {MaxCampaignDays} days.";
        return null;
    }

    public static string? CheckInduction(InductionInput input)
    {
        if (input.Pages is not { Count: > 0 and <= 20 })
            return "An induction has 1 to 20 pages.";
        if (input.Questions is not { Count: > 0 and <= 20 })
            return "An induction has 1 to 20 questions.";
        if (input.ValidMonths is < 1 or > 60)
            return "An induction is valid for 1 to 60 months.";
        foreach (var page in input.Pages)
            if (TextsProblem(page.Texts, t => (t.Title, t.Body)) is { } problem)
                return problem;
        for (var q = 0; q < input.Questions.Count; q++)
        {
            var question = input.Questions[q];
            if (!question.Texts.TryGetValue("en", out var en) || string.IsNullOrWhiteSpace(en.Question))
                return $"Question {q + 1} needs its English text.";
            if (en.Options is not { Count: >= 2 and <= 6 })
                return $"Question {q + 1} has 2 to 6 answers.";
            if (question.Correct < 0 || question.Correct >= en.Options.Count)
                return $"Question {q + 1}: choose the right answer.";
            foreach (var (language, text) in question.Texts)
            {
                if (!Languages.Contains(language))
                    return $"'{language}' is not a driver language (en, ar, ur, hi).";
                if (string.IsNullOrWhiteSpace(text.Question) || text.Question.Length > 300 || text.Options.Count != en.Options.Count
                    || text.Options.Any(o => string.IsNullOrWhiteSpace(o) || o.Length > 200))
                    return $"Question {q + 1} ({language}): the question and every answer, the same answers as in English.";
            }
        }
        return null;
    }

    /// <summary>The questions answered wrongly (an answer missing counts as wrong).</summary>
    public static IReadOnlyList<int> Wrong(IReadOnlyList<InductionQuestion> questions, IReadOnlyList<int>? answers) =>
        Enumerable.Range(0, questions.Count).Where(q => answers is null || q >= answers.Count || answers[q] != questions[q].Correct).ToList();

    /// <summary>The text in the language, else English.</summary>
    public static T Pick<T>(IReadOnlyDictionary<string, T> texts, string? language) =>
        texts.TryGetValue((language ?? "en").Trim().ToLowerInvariant(), out var text) ? text : texts["en"];

    private static string? TextsProblem<T>(IReadOnlyDictionary<string, T>? texts, Func<T, (string Title, string Body)> parts)
    {
        if (texts is null || !texts.TryGetValue("en", out var en) || string.IsNullOrWhiteSpace(parts(en).Title) || string.IsNullOrWhiteSpace(parts(en).Body))
            return "Write the title and the text in English (other languages are optional).";
        foreach (var (language, text) in texts)
        {
            if (!Languages.Contains(language))
                return $"'{language}' is not a driver language (en, ar, ur, hi).";
            var (title, body) = parts(text);
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(body))
                return $"Write both the title and the text in '{language}', or leave that language out.";
            if (title.Length > MaxTitle || body.Length > MaxBody)
                return $"A title is at most {MaxTitle} characters and a text {MaxBody}.";
        }
        return null;
    }
}
