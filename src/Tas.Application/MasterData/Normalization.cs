using System.Text;
using System.Text.RegularExpressions;

namespace Tas.Application.MasterData;

/// <summary>
/// Truck plates in one canonical form so the same truck matches however it was typed or read by gate OCR:
/// digits first, then letters, upper-case Latin, no spaces. Saudi plate letters are mapped from Arabic
/// to their official Latin equivalents and Arabic-Indic digits to 0–9.
///   "1234 ABJ", "ABJ-1234", "أ ب ح ١٢٣٤" → "1234ABJ"
/// </summary>
public static class PlateNumber
{
    private static readonly Dictionary<char, char> SaudiLetters = new()
    {
        ['ا'] = 'A', ['أ'] = 'A', ['إ'] = 'A', ['آ'] = 'A',
        ['ب'] = 'B', ['ح'] = 'J', ['د'] = 'D', ['ر'] = 'R', ['س'] = 'S', ['ص'] = 'X', ['ط'] = 'T',
        ['ع'] = 'E', ['ق'] = 'G', ['ك'] = 'K', ['ل'] = 'L', ['م'] = 'Z', ['ن'] = 'N',
        ['ه'] = 'H', ['ة'] = 'H', ['و'] = 'U', ['ى'] = 'V', ['ي'] = 'V',
    };

    public static string Normalize(string? value)
    {
        var digits = new StringBuilder();
        var letters = new StringBuilder();

        foreach (var c in value ?? "")
        {
            var digit = Digits.ToAscii(c);
            if (digit is not null)
                digits.Append(digit.Value);
            else if (SaudiLetters.TryGetValue(c, out var mapped))
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

/// <summary>Mobile numbers in E.164 ("+9665XXXXXXXX"). Saudi local forms (05…, 5…, 9665…, 009665…) are converted.</summary>
public static partial class PhoneNumber
{
    [GeneratedRegex(@"^\+[1-9][0-9]{7,14}$")]
    private static partial Regex E164();

    /// <summary>The E.164 form, or null when it is not a usable mobile number.</summary>
    public static string? Normalize(string? value)
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
        if (n.StartsWith("00", StringComparison.Ordinal)) n = "+" + n[2..];
        else if (n.StartsWith("05", StringComparison.Ordinal) && n.Length == 10) n = "+966" + n[1..];
        else if (n.StartsWith('5') && n.Length == 9) n = "+966" + n;
        else if (n.StartsWith("966", StringComparison.Ordinal)) n = "+" + n;

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
