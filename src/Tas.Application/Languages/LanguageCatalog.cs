using System.Text.Json;
using System.Text.RegularExpressions;

namespace Tas.Application.Languages;

/// <summary>A message in a language pack: the email subject (or push title) and the text.</summary>
public sealed record MessageText(string? Subject, string Body);

/// <summary>
/// A language pack: one JSON file per language (named after its two-letter code, e.g. fr.json) with any of three
/// parts — the portal's texts, the drivers' app's texts and the messages (SMS, email, push). Whatever a pack leaves
/// out falls back to English. A pack for a language Yardsyncs already has (ar, ur, hi) replaces those texts only, so
/// a native speaker's corrections need no new version.
/// </summary>
public sealed record LanguagePack(
    string Code,
    string Name,
    string Direction,
    IReadOnlyDictionary<string, MessageText> Messages,
    IReadOnlyDictionary<string, string> Portal,
    IReadOnlyDictionary<string, string> App);

/// <summary>A language the installation offers, and where.</summary>
public sealed record LanguageInfo(string Code, string Name, string Direction, bool Portal, bool App, bool Messages, bool FromPack);

/// <summary>
/// The languages of this installation: the built-in ones plus the packs found at start-up (set once, like the
/// installation's country).
/// </summary>
public static partial class LanguageCatalog
{
    /// <summary>Messages that are not notifications: the drivers' sign-in code and the password emails.</summary>
    public static class Messages
    {
        public const string SignInCode = "SIGN_IN_CODE";
        public const string PasswordInvite = "PASSWORD_INVITE";
        public const string PasswordReset = "PASSWORD_RESET";
    }

    public static IReadOnlyList<LanguageInfo> BuiltIn { get; } =
    [
        new("en", "English", "ltr", Portal: true, App: true, Messages: true, FromPack: false),
        new("ar", "العربية", "rtl", Portal: true, App: true, Messages: true, FromPack: false),
        new("ur", "اردو", "rtl", Portal: false, App: true, Messages: true, FromPack: false),
        new("hi", "हिन्दी", "ltr", Portal: false, App: true, Messages: true, FromPack: false),
    ];

    private static IReadOnlyDictionary<string, LanguagePack> _packs = new Dictionary<string, LanguagePack>();

    /// <summary>Puts the packs found at start-up in use.</summary>
    public static void Use(IEnumerable<LanguagePack> packs) => _packs = packs.ToDictionary(p => p.Code);

    public static LanguagePack? Pack(string? code) => code is null ? null : _packs.GetValueOrDefault(code.Trim().ToLowerInvariant());

    public static IReadOnlyCollection<LanguagePack> Packs => _packs.Values.OrderBy(p => p.Code).ToList();

    /// <summary>Every language: built-in ones with what their pack adds, then the new ones, by code.</summary>
    public static IReadOnlyList<LanguageInfo> All
    {
        get
        {
            var list = BuiltIn.Select(b => Pack(b.Code) is { } p
                ? b with { Portal = b.Portal || p.Portal.Count > 0, App = b.App || p.App.Count > 0, FromPack = true }
                : b).ToList();
            list.AddRange(_packs.Values.Where(p => BuiltIn.All(b => b.Code != p.Code)).OrderBy(p => p.Code)
                .Select(p => new LanguageInfo(p.Code, p.Name, p.Direction, p.Portal.Count > 0, p.App.Count > 0, p.Messages.Count > 0, true)));
            return list;
        }
    }

    /// <summary>Languages a person can get messages in (drivers' and users' preferred language).</summary>
    public static bool IsMessageLanguage(string? code) =>
        code is not null && All.Any(l => l.Messages && l.Code == code.Trim().ToLowerInvariant());

    /// <summary>The pack's text for a message, or null when the language has no pack text for it.</summary>
    public static MessageText? Message(string? language, string message) =>
        Pack(language)?.Messages.GetValueOrDefault(message);

    // ------------------------------------------------------------------ reading a pack

    [GeneratedRegex("^[a-z]{2}$")]
    private static partial Regex CodePattern();

    [GeneratedRegex(@"\{([a-zA-Z]+)\}")]
    private static partial Regex Placeholder();

    /// <summary>
    /// Reads a pack. <paramref name="allowedPlaceholders"/> gives, per message, the {placeholders} its text may use
    /// (unknown messages and placeholders are refused, so a typo can't send raw braces to a driver).
    /// </summary>
    public static (LanguagePack? Pack, IReadOnlyList<string> Problems) Parse(
        string fileCode, string json, IReadOnlyDictionary<string, IReadOnlySet<string>> allowedPlaceholders)
    {
        var problems = new List<string>();
        JsonElement root;
        try
        {
            root = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }).RootElement;
        }
        catch (JsonException ex)
        {
            return (null, [$"not valid JSON ({ex.Message})"]);
        }
        if (root.ValueKind != JsonValueKind.Object)
            return (null, ["the file must hold one JSON object"]);

        string? Str(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()?.Trim() : null;

        var code = Str("code")?.ToLowerInvariant() ?? fileCode;
        if (!CodePattern().IsMatch(code))
            problems.Add($"code '{code}' must be a two-letter language code such as fr");
        if (code != fileCode)
            problems.Add($"code '{code}' must match the file name ({fileCode}.json)");
        var name = Str("name");
        if (string.IsNullOrEmpty(name))
            problems.Add("name is required (the language's own name, e.g. Français)");
        var direction = (Str("direction") ?? "ltr").ToLowerInvariant();
        if (direction is not ("ltr" or "rtl"))
            problems.Add("direction must be ltr or rtl");

        var portal = Texts(root, "portal", problems);
        var app = Texts(root, "app", problems);

        var messages = new Dictionary<string, MessageText>();
        if (root.TryGetProperty("messages", out var m))
        {
            if (m.ValueKind != JsonValueKind.Object)
                problems.Add("messages must be an object");
            else
                foreach (var message in m.EnumerateObject())
                {
                    if (!allowedPlaceholders.TryGetValue(message.Name, out var allowed))
                    {
                        problems.Add($"messages.{message.Name} is not a Yardsyncs message (see template.json)");
                        continue;
                    }
                    string? Part(string part) => message.Value.ValueKind == JsonValueKind.Object
                        && message.Value.TryGetProperty(part, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
                    var body = Part("body");
                    if (string.IsNullOrWhiteSpace(body))
                    {
                        problems.Add($"messages.{message.Name}.body is required");
                        continue;
                    }
                    var subject = Part("subject");
                    foreach (var text in new[] { subject, body }.OfType<string>())
                        foreach (Match p in Placeholder().Matches(text))
                            if (!allowed.Contains(p.Groups[1].Value))
                                problems.Add($"messages.{message.Name} uses {{{p.Groups[1].Value}}}; it can use {string.Join(", ", allowed.Select(a => "{" + a + "}"))}");
                    messages[message.Name] = new MessageText(string.IsNullOrWhiteSpace(subject) ? null : subject, body);
                }
        }

        if (portal.Count == 0 && app.Count == 0 && messages.Count == 0)
            problems.Add("the pack has no texts (portal, app or messages)");

        return problems.Count > 0 ? (null, problems) : (new LanguagePack(code, name!, direction, messages, portal, app), problems);
    }

    private static Dictionary<string, string> Texts(JsonElement root, string part, List<string> problems)
    {
        var texts = new Dictionary<string, string>();
        if (!root.TryGetProperty(part, out var section))
            return texts;
        if (section.ValueKind != JsonValueKind.Object)
        {
            problems.Add($"{part} must be an object of key → text");
            return texts;
        }
        foreach (var entry in section.EnumerateObject())
        {
            if (entry.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(entry.Value.GetString()))
                texts[entry.Name] = entry.Value.GetString()!;
            else if (entry.Value.ValueKind != JsonValueKind.String)
                problems.Add($"{part}.{entry.Name} must be text");
        }
        return texts;
    }
}
