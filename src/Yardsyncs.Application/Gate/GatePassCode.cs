using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;

namespace Yardsyncs.Application.Gate;

/// <summary>
/// The rotating gate pass in the driver app: a QR "YS1:{passId}:{appointmentNo}:{code}" where the code changes every
/// 30 seconds (HMAC-SHA256 over the time step, TOTP-style, 8 digits). The app makes it offline from a secret it got at
/// sign-in; a screenshot stops working within a minute.
/// </summary>
public static class GatePassCode
{
    public const string Prefix = "YS1";
    public const int StepSeconds = 30;
    public const int Digits = 8;

    /// <summary>Codes of the previous and next step are accepted too (phone clocks drift; reading takes time).</summary>
    public const int Window = 1;

    public static long Step(DateTimeOffset at) => at.ToUnixTimeSeconds() / StepSeconds;

    public static string Compute(byte[] secret, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, step);
        var hash = HMACSHA256.HashData(secret, counter);
        var offset = hash[^1] & 0x0F;
        var value = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (value % 100_000_000).ToString("D8", CultureInfo.InvariantCulture);
    }

    public static bool Verify(byte[] secret, string code, DateTimeOffset now)
    {
        var step = Step(now);
        var expected = code.Trim();
        for (var s = step - Window; s <= step + Window; s++)
        {
            if (CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.ASCII.GetBytes(Compute(secret, s)), System.Text.Encoding.ASCII.GetBytes(expected)))
                return true;
        }
        return false;
    }

    public static string Payload(long passId, string appointmentNo, string code) =>
        $"{Prefix}:{passId.ToString(CultureInfo.InvariantCulture)}:{appointmentNo}:{code}";

    /// <summary>Reads a gate pass QR; false for anything else (e.g. a static appointment QR).</summary>
    public static bool TryParse(string? payload, out long passId, out string appointmentNo, out string code)
    {
        passId = 0;
        appointmentNo = "";
        code = "";
        var parts = payload?.Trim().Split(':');
        if (parts is not { Length: 4 } || !string.Equals(parts[0], Prefix, StringComparison.OrdinalIgnoreCase)
            || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out passId)
            || parts[2].Length is 0 or > 30 || parts[3].Length != Digits || !parts[3].All(char.IsAsciiDigit))
            return false;
        appointmentNo = parts[2].ToUpperInvariant();
        code = parts[3];
        return true;
    }
}
