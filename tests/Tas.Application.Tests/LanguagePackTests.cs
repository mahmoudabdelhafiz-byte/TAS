using Tas.Application.Languages;
using Tas.Application.MasterData;
using Tas.Application.Notifications;
using Tas.Domain;

namespace Tas.Application.Tests;

/// <summary>Language packs: reading them, refusing mistakes, and messages in a pack's language.</summary>
[Collection(nameof(LanguageCatalogCollection))]
public class LanguagePackTests
{
    private const string Tagalog = """
        {
          "code": "tl", "name": "Tagalog", "direction": "ltr",
          "portal": { "nav.book": "Mag-book ng trak" },
          "app": { "signOut": "Mag-sign out" },
          "messages": {
            "STATUS_RED": { "subject": "Naka-hold ang {appointmentNo}", "body": "Yardsyncs: naka-hold ang {appointmentNo} ({slot}): {reason}." },
            "SIGN_IN_CODE": { "body": "Yardsyncs: code {code}, {minutes} minuto." }
          }
        }
        """;

    private static (LanguagePack? Pack, IReadOnlyList<string> Problems) Parse(string code, string json) =>
        LanguageCatalog.Parse(code, json, NotificationText.Placeholders);

    [Fact]
    public void A_pack_is_read_with_its_three_parts()
    {
        var (pack, problems) = Parse("tl", Tagalog);
        Assert.Empty(problems);
        Assert.Equal(("tl", "Tagalog", "ltr"), (pack!.Code, pack.Name, pack.Direction));
        Assert.Equal("Mag-book ng trak", pack.Portal["nav.book"]);
        Assert.Equal("Mag-sign out", pack.App["signOut"]);
        Assert.Equal(2, pack.Messages.Count);
        Assert.Null(pack.Messages[LanguageCatalog.Messages.SignInCode].Subject);
    }

    [Theory]
    [InlineData("tl", """{ "name": "X", "messages": { "STATUS_RED": { "body": "{appointmentNo} {driverName}" } } }""", "{driverName}")]
    [InlineData("tl", """{ "name": "X", "messages": { "WELCOME": { "body": "hi" } } }""", "WELCOME is not a Yardsyncs message")]
    [InlineData("tl", """{ "name": "X", "messages": { "STATUS_RED": { "subject": "x" } } }""", "body is required")]
    [InlineData("tl", """{ "code": "fil", "name": "X", "portal": { "a": "b" } }""", "two-letter")]
    [InlineData("tl", """{ "code": "fr", "name": "X", "portal": { "a": "b" } }""", "must match the file name")]
    [InlineData("tl", """{ "portal": { "a": "b" } }""", "name is required")]
    [InlineData("tl", """{ "name": "X", "direction": "up", "portal": { "a": "b" } }""", "ltr or rtl")]
    [InlineData("tl", """{ "name": "X" }""", "no texts")]
    [InlineData("tl", """{ "name": "X", "portal": { "a": 1 } }""", "portal.a must be text")]
    [InlineData("tl", "not json", "not valid JSON")]
    public void Mistakes_are_refused_with_what_is_wrong(string code, string json, string problem)
    {
        var (pack, problems) = Parse(code, json);
        Assert.Null(pack);
        Assert.Contains(problems, p => p.Contains(problem, StringComparison.Ordinal));
    }

    [Fact]
    public void Messages_use_the_pack_then_the_built_in_text_then_english()
    {
        var values = new Dictionary<string, string> { [NotificationText.Keys.AppointmentNo] = "X-1", [NotificationText.Keys.Slot] = "10:00", [NotificationText.Keys.Reason] = "hold" };
        var arabicFix = Parse("ar", """{ "name": "العربية", "direction": "rtl", "messages": { "STATUS_CLEARED": { "body": "Yardsyncs: الموعد {appointmentNo} جاهز." } } }""").Pack!;
        try
        {
            LanguageCatalog.Use([Parse("tl", Tagalog).Pack!, arabicFix]);

            var tagalog = NotificationText.Render(NotificationTemplates.StatusRed, "tl", values, "Harbor One");
            Assert.Equal("Harbor One — Naka-hold ang X-1", tagalog.Subject);
            Assert.Equal("Harbor One: naka-hold ang X-1 (10:00): hold.", tagalog.Body);
            Assert.StartsWith("Yardsyncs: appointment X-1", NotificationText.Render(NotificationTemplates.StatusCleared, "tl", values).Body);

            // A correction to a built-in language: that text only; its subject and the rest stay built in.
            var cleared = NotificationText.Render(NotificationTemplates.StatusCleared, "ar", values);
            Assert.Equal("Yardsyncs: الموعد X-1 جاهز.", cleared.Body);
            Assert.Contains("الإفراج", cleared.Subject);
            Assert.Contains("موقوف", NotificationText.Render(NotificationTemplates.StatusRed, "ar", values).Subject);

            Assert.Contains("tl", MasterDataValidator.Languages);
            Assert.Contains(LanguageCatalog.All, l => l is { Code: "tl", Portal: true, App: true, Messages: true, FromPack: true });
            Assert.Contains(LanguageCatalog.All, l => l is { Code: "ar", FromPack: true });
        }
        finally
        {
            LanguageCatalog.Use([]);
        }
        Assert.DoesNotContain("tl", MasterDataValidator.Languages);
        Assert.Equal(new[] { "en", "ar", "ur", "hi" }, LanguageCatalog.All.Select(l => l.Code));
    }

    [Fact]
    public void The_translators_template_holds_every_message_in_english()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "deploy", "languages", "template.json")))
            folder = folder.Parent;
        Assert.NotNull(folder);
        var json = File.ReadAllText(Path.Combine(folder.FullName, "deploy", "languages", "template.json"));

        // The template, given a code and a name, is a valid pack.
        var named = json.Replace("\"code\": \"\"", "\"code\": \"xx\"").Replace("\"name\": \"\"", "\"name\": \"Template\"");
        var (pack, problems) = Parse("xx", named);
        Assert.Empty(problems);
        Assert.Equal(NotificationText.MessageCodes.Order(), pack!.Messages.Keys.Order());
        foreach (var code in NotificationText.MessageCodes)
            Assert.Equal(NotificationText.English(code), (pack.Messages[code].Subject!, pack.Messages[code].Body));
    }
}

/// <summary>Tests that change the installation's languages run one at a time.</summary>
[CollectionDefinition(nameof(LanguageCatalogCollection), DisableParallelization = true)]
public sealed class LanguageCatalogCollection;
