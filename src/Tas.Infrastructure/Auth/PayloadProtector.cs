using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Tas.Infrastructure.Auth;

/// <summary>
/// Encrypts short secrets that must wait in the database for a moment (a password link in the notification
/// outbox) with AES-256-GCM. The key is derived from Jwt:SigningKey, which every API instance already shares,
/// so no extra secret or key store is needed. Changing the signing key makes queued payloads unreadable
/// (those messages fail and the user simply asks for a new link).
/// </summary>
public sealed class PayloadProtector
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private static readonly byte[] Info = Encoding.UTF8.GetBytes("tas/notification-payload/v1");

    private readonly byte[] _key;

    public PayloadProtector(IOptions<JwtOptions> options)
    {
        var secret = Encoding.UTF8.GetBytes(options.Value.SigningKey ?? "");
        if (secret.Length < 32)
            throw new InvalidOperationException("Jwt:SigningKey must be at least 32 characters.");
        _key = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, salt: null, info: Info);
    }

    public string Protect(string plaintext)
    {
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var output = new byte[NonceSize + TagSize + plain.Length];
        var nonce = output.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plain, output.AsSpan(NonceSize + TagSize), output.AsSpan(NonceSize, TagSize));
        return Convert.ToBase64String(output);
    }

    /// <summary>Null if the payload is damaged or was encrypted with another key.</summary>
    public string? Unprotect(string? protectedText)
    {
        if (string.IsNullOrEmpty(protectedText))
            return null;
        try
        {
            var input = Convert.FromBase64String(protectedText);
            if (input.Length < NonceSize + TagSize)
                return null;

            var plain = new byte[input.Length - NonceSize - TagSize];
            using var aes = new AesGcm(_key, TagSize);
            aes.Decrypt(input.AsSpan(0, NonceSize), input.AsSpan(NonceSize + TagSize), input.AsSpan(NonceSize, TagSize), plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return null;
        }
    }
}
