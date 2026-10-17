using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Yardsyncs.Domain.Parties;

namespace Yardsyncs.Application.Auth;

/// <summary>
/// Two-step sign-in with an authenticator app (RFC 6238 time-based codes: 6 digits, 30 seconds, SHA-1 — what Google
/// Authenticator, Microsoft Authenticator and the others read).
/// </summary>
public static class Totp
{
    public const int Digits = 6;
    public const int StepSeconds = 30;

    /// <summary>The code of the step before and after is accepted too (clocks drift; typing takes time).</summary>
    public const int Window = 1;

    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(20);

    public static long Step(DateTimeOffset at) => at.ToUnixTimeSeconds() / StepSeconds;

    public static string Code(byte[] secret, long step, int digits = Digits)
    {
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, step);
        var hash = HMACSHA1.HashData(secret, counter);
        var offset = hash[^1] & 0x0F;
        var value = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (value % (int)Math.Pow(10, digits)).ToString(new string('0', digits), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The step the code belongs to, when it is right and newer than <paramref name="lastUsedStep"/> (a code works
    /// once); null otherwise.
    /// </summary>
    public static long? Verify(byte[] secret, string? code, DateTimeOffset now, long? lastUsedStep)
    {
        var typed = new string((code ?? "").Where(char.IsDigit).ToArray());
        if (typed.Length != Digits)
            return null;
        var step = Step(now);
        for (var s = step - Window; s <= step + Window; s++)
        {
            if ((lastUsedStep is null || s > lastUsedStep)
                && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Code(secret, s)), Encoding.ASCII.GetBytes(typed)))
                return s;
        }
        return null;
    }

    /// <summary>The secret as authenticator apps take it when typed in (base32, groups of four).</summary>
    public static string Base32(byte[] data)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var text = new StringBuilder();
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                text.Append(alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0)
            text.Append(alphabet[(buffer << (5 - bits)) & 31]);
        return text.ToString();
    }

    /// <summary>Base32 back to bytes (spaces and case ignored).</summary>
    public static byte[] FromBase32(string text)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in text.ToUpperInvariant().Where(c => c != ' ' && c != '='))
        {
            var value = alphabet.IndexOf(c);
            if (value < 0)
                throw new FormatException($"'{c}' is not base32.");
            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }
        return [.. bytes];
    }

    /// <summary>The otpauth:// link an authenticator app opens (or reads from a QR code).</summary>
    public static string Uri(string issuer, string account, byte[] secret) =>
        $"otpauth://totp/{System.Uri.EscapeDataString(issuer)}:{System.Uri.EscapeDataString(account)}" +
        $"?secret={Base32(secret)}&issuer={System.Uri.EscapeDataString(issuer)}&digits={Digits}&period={StepSeconds}";

    /// <summary>Roles that change what others can do: two-step sign-in is required for them (Auth:AdminTwoStep).</summary>
    public static bool IsAdminRole(UserRole role) => role is UserRole.TerminalAdmin or UserRole.OperatorAdmin or UserRole.SysAdmin;
}
