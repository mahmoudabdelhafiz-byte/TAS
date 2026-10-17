using System.Security.Cryptography;
using Yardsyncs.Application.Gate;
using Yardsyncs.Domain.Parties;

namespace Yardsyncs.Application.Auth;

// A trucking company's own system (TMS, dispatch) on the API with a key in X-Api-Key, instead of a person's sign-in.

/// <summary>A new key: a name to recognise it by, and what it may do.</summary>
public sealed record CompanyApiKeyInput(string Name, ApiKeyScope Scope = ApiKeyScope.Book);

/// <summary>A key as the company's admin sees it: never the key itself.</summary>
public sealed record CompanyApiKeyView(
    int KeyId, int CompanyId, string Name, ApiKeyScope Scope, string KeyHint, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt, DateTimeOffset? RevokedAt);

/// <summary>A key just made: shown once; only its fingerprint is kept.</summary>
public sealed record CompanyApiKeyIssued(CompanyApiKeyView ApiKey, string Key);

public static class CompanyApiKeys
{
    public const string Prefix = "ysc_";

    /// <summary>The claim a read-only key carries; such a request may only read (GET).</summary>
    public const string ReadOnlyClaim = "api_read_only";

    /// <summary>Most keys a company can have in use at once.</summary>
    public const int MaxActive = 10;

    public static string New() => Prefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static bool LooksLikeKey(string? key) => key is { Length: > 20 and < 100 } && key.StartsWith(Prefix, StringComparison.Ordinal);

    public static string Hash(string key) => GateSystemKeys.Hash(key);

    public static string Hint(string key) => GateSystemKeys.Hint(key);

    public static IReadOnlyList<string> Validate(CompanyApiKeyInput input)
    {
        var errors = new List<string>();
        var name = input.Name?.Trim() ?? "";
        if (name.Length == 0) errors.Add("Give the key a name, e.g. \"Our TMS\".");
        else if (name.Length > 60) errors.Add("The name is at most 60 characters.");
        if (!Enum.IsDefined(input.Scope)) errors.Add("The scope is Book or Read.");
        return errors;
    }

    /// <summary>Read-only keys may only read.</summary>
    public static bool Allows(bool readOnly, string method) =>
        !readOnly || HttpMethodIsRead(method);

    private static bool HttpMethodIsRead(string method) =>
        method.Equals("GET", StringComparison.OrdinalIgnoreCase) || method.Equals("HEAD", StringComparison.OrdinalIgnoreCase);
}
