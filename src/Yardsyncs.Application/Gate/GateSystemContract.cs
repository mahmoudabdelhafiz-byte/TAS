using System.Security.Cryptography;
using System.Text;

namespace Yardsyncs.Application.Gate;

// The terminal's own gate system on the gate API (X-Api-Key), for a truck appointment system without Yardsyncs Autogate.

/// <summary>A new gate system key: a name to recognise it by.</summary>
public sealed record GateSystemInput(string Name);

/// <summary>A gate system key as terminal admins see it: never the key itself.</summary>
public sealed record GateSystemView(
    int KeyId, string Name, string KeyHint, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt, DateTimeOffset? RevokedAt);

/// <summary>A key just made: shown once; only its fingerprint is kept.</summary>
public sealed record GateSystemKeyIssued(GateSystemView System, string Key);

public static class GateSystemKeys
{
    public const string Header = "X-Api-Key";
    public const string Prefix = "ysg_";

    public static string New() => Prefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Hash(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key.Trim())));

    public static bool LooksLikeKey(string? key) => key is { Length: > 20 and < 100 } && key.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>The last characters, shown to tell keys apart.</summary>
    public static string Hint(string key) => "…" + key[^6..];

    public static IReadOnlyList<string> Validate(GateSystemInput input)
    {
        var name = input.Name?.Trim() ?? "";
        if (name.Length == 0) return ["Give the gate system a name, e.g. \"North gate OCR portal\"."];
        if (name.Length > 60) return ["The name is at most 60 characters."];
        return [];
    }
}
