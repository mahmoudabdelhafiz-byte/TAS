using System.Text;
using System.Text.RegularExpressions;
using Tas.Application.Countries;

namespace Tas.Application.MasterData;

/// <summary>
/// Truck plates in one canonical form so the same truck matches however it was typed or read by gate OCR:
/// digits first, then letters, upper-case Latin, no spaces. Arabic plate letters are mapped to the Latin letter they
/// stand for in the country pack (Saudi plates today), Arabic-Indic digits to 0–9.
///   "1234 ABJ", "ABJ-1234", "أ ب ح ١٢٣٤" → "1234ABJ"
/// </summary>
public static class PlateNumber
{
    /// <summary>Normalizes with the installation's country pack.</summary>
    public static string Normalize(string? value) => Normalize(value, CountryPacks.Installation);

    public static string Normalize(string? value, CountryPack country)
    {
        var digits = new StringBuilder();
        var letters = new StringBuilder();

        foreach (var c in value ?? "")
        {
            var digit = Digits.ToAscii(c);
            if (digit is not null)
                digits.Append(digit.Value);
            else if (country.PlateLetters.TryGetValue(c, out var mapped))
                letters.Append(mapped);
            else if (c is >= 'a' and <= 'z' or >= 'A' and <= 'Z')
                letters.Append(char.ToUpperInvariant(c));
        }

        return digits.ToString() + letters;
    }

    /// <summary>At least one digit, 2–12 characters once normalized.</summary>
    public static bool IsValid(string normalized) =>
        normalized.Length is >= 2 and <= 12 && normalized.Any(char.IsAsciiDigit);
}

/// <summary>
/// Mobile numbers in E.164 ("+9665XXXXXXXX"). Numbers typed with "+" or "00" work from any country; local forms
/// follow the country pack: in Saudi Arabia 05…, 5… and 9665… become +9665….
/// </summary>
public static partial class PhoneNumber
{
    [GeneratedRegex(@"^\+[1-9][0-9]{7,14}$")]
    private static partial Regex E164();

    /// <summary>The E.164 form with the installation's country pack, or null when it is not a usable mobile number.</summary>
    public static string? Normalize(string? value) => Normalize(value, CountryPacks.Installation);

    public static string? Normalize(string? value, CountryPack country)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var sb = new StringBuilder();
        foreach (var c in value.Trim())
        {
            if (c == '+' && sb.Length == 0)
                sb.Append('+');
            else if (Digits.ToAscii(c) is { } d)
                sb.Append(d);
        }

        var n = sb.ToString();
        var rule = country.Mobile;
        bool Mobile(string national) => national.Length == rule.Length && rule.Starts.Any(national.StartsWith);

        if (n.StartsWith("00", StringComparison.Ordinal)) n = "+" + n[2..];
        else if (n.StartsWith('+')) { }
        else if (rule.TrunkPrefix.Length > 0 && n.StartsWith(rule.TrunkPrefix, StringComparison.Ordinal) && Mobile(n[rule.TrunkPrefix.Length..]))
            n = "+" + rule.DialCode + n[rule.TrunkPrefix.Length..];
        else if (Mobile(n)) n = "+" + rule.DialCode + n;
        else if (n.StartsWith(rule.DialCode, StringComparison.Ordinal)) n = "+" + n;

        return E164().IsMatch(n) ? n : null;
    }
}

internal static class Digits
{
    /// <summary>ASCII digit for '0'–'9', Arabic-Indic '٠'–'٩' and Extended Arabic-Indic '۰'–'۹'.</summary>
    public static char? ToAscii(char c) => c switch
    {
        >= '0' and <= '9' => c,
        >= '٠' and <= '٩' => (char)('0' + (c - '٠')),
        >= '۰' and <= '۹' => (char)('0' + (c - '۰')),
        _ => null,
    };
}
