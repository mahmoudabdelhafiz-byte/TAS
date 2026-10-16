using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Tas.Application.Countries;
using Tas.Infrastructure.Notifications;
using Tas.Infrastructure.Persistence;

namespace Tas.Infrastructure;

/// <summary>
/// Settings one terminal can have differently from the rest of the installation, by terminal code — so one cloud
/// installation serves several operators. Anything a terminal doesn't set falls back to the installation's setting:
///
///   Terminals__JED1__Tos=N4                               the TOS adapter (Fake or N4; default Tos:Adapter)
///   Terminals__JED1__Brand=HARBOR1                         the name in messages (default Brand, then "Yardsyncs")
///   Terminals__JED1__Country=SA                            holidays and currency (default Country, then SA)
///   Terminals__JED1__Notifications__SmsProvider=Unifonic   any Notifications setting: providers, accounts,
///   Terminals__JED1__Notifications__Unifonic__SenderId=HARBOR1   SMS sender name, email sender address and name
///
/// Secrets stay in environment variables, as for the installation-wide settings.
/// </summary>
public sealed class TerminalProfiles(IConfiguration config)
{
    public const string DefaultBrand = "Yardsyncs";
    public static readonly string[] TosAdapters = ["Fake", "N4"];

    private static readonly ConcurrentDictionary<int, string> Codes = new();

    /// <summary>The terminal's code (cached; codes don't change).</summary>
    public static async Task<string?> CodeAsync(TasDbContext db, int terminalId, CancellationToken ct = default)
    {
        if (Codes.TryGetValue(terminalId, out var code))
            return code;
        code = await db.Terminals.AsNoTracking().Where(t => t.TerminalId == terminalId).Select(t => t.Code).SingleOrDefaultAsync(ct);
        if (code is not null)
            Codes[terminalId] = code;
        return code;
    }

    /// <summary>The code when it was looked up before, else null.</summary>
    public static string? CachedCode(int terminalId) => Codes.GetValueOrDefault(terminalId);

    public string DefaultTos => Normalize(config["Tos:Adapter"]) ?? "Fake";

    public string TosFor(string? code) => (code is null ? null : Normalize(config[$"Terminals:{code}:Tos"])) ?? DefaultTos;

    /// <summary>The terminal's own Brand setting, or null when it has none.</summary>
    public string? OwnBrand(string? code) => code is null ? null : Clean(config[$"Terminals:{code}:Brand"]);

    public string BrandFor(string? code) =>
        (code is null ? null : Clean(config[$"Terminals:{code}:Brand"])) ?? Clean(config["Brand"]) ?? DefaultBrand;

    /// <summary>The installation's country (setting Country): reads mobile numbers and plates for every terminal.</summary>
    public CountryPack InstallationCountry => CountryPacks.Find(config["Country"]) ?? CountryPacks.SaudiArabia;

    /// <summary>The terminal's country, for its public holidays and currency.</summary>
    public CountryPack CountryFor(string? code) =>
        (code is null ? null : CountryPacks.Find(config[$"Terminals:{code}:Country"])) ?? InstallationCountry;

    public bool HasOwnNotifications(string? code) => code is not null && config.GetSection($"Terminals:{code}:Notifications").Exists();

    /// <summary>The installation's notification settings with the terminal's own on top.</summary>
    public NotificationOptions NotificationsFor(string? code)
    {
        var options = new NotificationOptions();
        config.GetSection("Notifications").Bind(options);
        if (code is not null)
            config.GetSection($"Terminals:{code}:Notifications").Bind(options);
        return options;
    }

    /// <summary>Terminal codes that have settings of their own.</summary>
    public IEnumerable<string> ConfiguredTerminals() => config.GetSection("Terminals").GetChildren().Select(c => c.Key);

    /// <summary>Stops the app from starting with a TOS name it doesn't know (a typo would otherwise show up as failed bookings).</summary>
    public void Validate()
    {
        var problems = new List<string>();
        if (!TosAdapters.Contains(DefaultTos, StringComparer.OrdinalIgnoreCase))
            problems.Add($"Unknown TOS adapter '{config["Tos:Adapter"]}' in Tos:Adapter.");
        var countries = string.Join(", ", CountryPacks.All.Select(p => p.Code));
        if (Clean(config["Country"]) is { } country && CountryPacks.Find(country) is null)
            problems.Add($"Unknown country '{country}' in Country (Yardsyncs has: {countries}).");
        foreach (var code in ConfiguredTerminals())
        {
            var tos = Normalize(config[$"Terminals:{code}:Tos"]);
            if (tos is not null && !TosAdapters.Contains(tos, StringComparer.OrdinalIgnoreCase))
                problems.Add($"Unknown TOS adapter '{tos}' in Terminals:{code}:Tos.");
            if (Clean(config[$"Terminals:{code}:Country"]) is { } own && CountryPacks.Find(own) is null)
                problems.Add($"Unknown country '{own}' in Terminals:{code}:Country (Yardsyncs has: {countries}).");
        }
        if (problems.Count > 0)
            throw new InvalidOperationException(string.Join(" ", problems) + " TOS adapters: " + string.Join(" or ", TosAdapters) + ".");
    }

    private static string? Normalize(string? value)
    {
        var v = Clean(value);
        return v is null ? null : TosAdapters.FirstOrDefault(a => a.Equals(v, StringComparison.OrdinalIgnoreCase)) ?? v;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
