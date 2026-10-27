using System.Text.RegularExpressions;

namespace Yardsyncs.Application.Booking;

/// <summary>
/// Vehicle identification numbers (ISO 3779): 17 letters and digits, never I, O or Q. The check digit (9th character)
/// is only used in North America, so it is not required here.
/// </summary>
public static partial class Vin
{
    [GeneratedRegex("^[A-HJ-NPR-Z0-9]{17}$")]
    private static partial Regex Format();

    /// <summary>Upper-cases and removes spaces and dashes ("wdd 205-0421 r123456" → "WDD2050421R123456").</summary>
    public static string Normalize(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    public static bool HasValidFormat(string normalized) => Format().IsMatch(normalized);
}
