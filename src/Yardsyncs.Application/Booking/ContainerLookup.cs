namespace Yardsyncs.Application.Booking;

/// <summary>
/// Containers to book in one go: either an import bill of lading (the TOS lists its containers) or a pasted list
/// of container numbers. Each is checked like a booking, so the dispatcher sees what can go before choosing hours.
/// </summary>
/// <param name="BookingRef">Export booking for an export drop-off list (applies to every container).</param>
public sealed record ContainerLookupInput(
    string Category,
    string? BlNo = null,
    IReadOnlyList<string>? Containers = null,
    string? BookingRef = null);

public enum LookupStatus
{
    /// <summary>All checks passed.</summary>
    Ready,

    /// <summary>Can be booked; something to keep an eye on (e.g. storage starts today).</summary>
    Warning,

    /// <summary>A check failed (hold, not discharged, wrong number…): cannot be booked now.</summary>
    Blocked,

    /// <summary>Already on a live appointment.</summary>
    Booked,
}

/// <param name="SizeFt">Length from the ISO type (20, 40, 45…), null when unknown.</param>
/// <param name="AppointmentNo">The live appointment holding the container, when the user may see it.</param>
public sealed record LookupContainer(
    string ContainerNo,
    string? IsoType,
    int? SizeFt,
    LookupStatus Status,
    IReadOnlyList<string> Problems,
    IReadOnlyList<string> Warnings,
    string? AppointmentNo);

public sealed record ContainerLookupResult(string Category, string? BlNo, IReadOnlyList<LookupContainer> Containers);

public static class IsoSizeType
{
    /// <summary>Container length in feet from the first character of an ISO 6346 size-type code ("22G1" → 20, "45G1" → 40, "L5G1" → 45).</summary>
    public static int? LengthFeet(string? isoType)
    {
        if (string.IsNullOrWhiteSpace(isoType))
            return null;
        return char.ToUpperInvariant(isoType.Trim()[0]) switch
        {
            '1' => 10,
            '2' => 20,
            '3' => 30,
            '4' => 40,
            'L' or 'M' => 45,
            _ => null,
        };
    }

    /// <summary>Splits pasted text (new lines, commas, semicolons, tabs, spaces) into container numbers.</summary>
    public static IReadOnlyList<string> SplitContainers(IEnumerable<string> values)
    {
        var result = new List<string>();
        foreach (var value in values)
        {
            foreach (var part in value.Split(new[] { '\n', '\r', ',', ';', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                // "MSCU 123456 6" stays one number; "MSCU1234566 TGHU8812340" is two.
                var compact = ContainerNumber.Normalize(part);
                if (compact.Length > 11 && compact.Length % 11 == 0 && part.Contains(' '))
                {
                    result.AddRange(part.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(ContainerNumber.Normalize).Where(p => p.Length > 0));
                    continue;
                }
                if (compact.Length > 0)
                    result.Add(compact);
            }
        }
        return result.Distinct().ToList();
    }
}
