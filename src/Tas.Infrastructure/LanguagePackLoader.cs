using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Tas.Application.Languages;
using Tas.Application.Notifications;

namespace Tas.Infrastructure;

/// <summary>
/// Reads the language packs in the folder <c>Languages:Path</c> (default: <c>languages</c> next to the app, which the
/// install kit mounts from <c>./languages</c>). One file per language, named after its two-letter code (fr.json);
/// other files (template.json, README.md) are ignored. A pack with a mistake stops the server from starting, with
/// what is wrong, so a typo never reaches a driver's phone.
/// </summary>
public static partial class LanguagePackLoader
{
    [GeneratedRegex("^[a-z]{2}$")]
    private static partial Regex FileCode();

    public static string FolderFor(IConfiguration config) =>
        config["Languages:Path"] is { Length: > 0 } path ? path : Path.Combine(AppContext.BaseDirectory, "languages");

    public static IReadOnlyList<LanguagePack> Load(string folder)
    {
        if (!Directory.Exists(folder))
            return [];

        var packs = new List<LanguagePack>();
        var problems = new List<string>();
        foreach (var file in Directory.EnumerateFiles(folder, "*.json").Order(StringComparer.Ordinal))
        {
            var code = Path.GetFileNameWithoutExtension(file);
            if (!FileCode().IsMatch(code))
                continue;
            var (pack, errors) = LanguageCatalog.Parse(code, File.ReadAllText(file), NotificationText.Placeholders);
            if (pack is null)
                problems.AddRange(errors.Select(e => $"{Path.GetFileName(file)}: {e}"));
            else
                packs.Add(pack);
        }

        if (problems.Count > 0)
            throw new InvalidOperationException($"Language packs in {folder} have mistakes: " + string.Join("; ", problems) + ".");
        return packs;
    }
}
