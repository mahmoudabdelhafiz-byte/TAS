using System.Text.RegularExpressions;

namespace Yardsyncs.Application.Booking;

/// <summary>ISO 6346 container numbers, e.g. CSQU3054383 (owner code, category U/J/Z, serial, check digit).</summary>
public static partial class ContainerNumber
{
    [GeneratedRegex("^[A-Z]{3}[UJZ][0-9]{7}$")]
    private static partial Regex Format();

    /// <summary>Upper-cases and removes spaces, dashes and slashes ("cs qu 305438-3" → "CSQU3054383").</summary>
    public static string Normalize(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    public static bool HasValidFormat(string normalized) => Format().IsMatch(normalized);

    /// <summary>True when the format is valid and the 11th character matches the ISO 6346 check digit.</summary>
    public static bool IsValid(string normalized) =>
        HasValidFormat(normalized) && CheckDigit(normalized) == normalized[10] - '0';

    /// <summary>ISO 6346 check digit of the first 10 characters.</summary>
    public static int CheckDigit(string normalized)
    {
        var sum = 0;
        for (var i = 0; i < 10; i++)
            sum += CharValue(normalized[i]) * (1 << i);
        return sum % 11 % 10;
    }

    // Letters map to 10..38, skipping multiples of 11 (11, 22, 33).
    private static int CharValue(char c)
    {
        if (char.IsDigit(c))
            return c - '0';

        var value = 10 + (c - 'A');
        if (value >= 11) value++;
        if (value >= 22) value++;
        if (value >= 33) value++;
        return value;
    }
}
