using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Yardsyncs.Application.Notifications;
using Yardsyncs.Domain;

namespace Yardsyncs.Infrastructure.Notifications;

/// <summary>
/// Firebase Cloud Messaging (HTTP v1) — push to the driver app on Android and iPhone (Firebase relays to Apple's
/// push service). Signs in with a Google service account: the private key belongs in an environment variable
/// (Notifications__Fcm__PrivateKey), never in a committed file.
/// </summary>
public sealed class FcmOptions
{
    public string ProjectId { get; set; } = "";

    /// <summary>The service account's client_email.</summary>
    public string ClientEmail { get; set; } = "";

    /// <summary>The service account's private_key (PEM; "\n" escapes are accepted).</summary>
    public string PrivateKey { get; set; } = "";

    public string TokenUri { get; set; } = "https://oauth2.googleapis.com/token";
    public string BaseUrl { get; set; } = "https://fcm.googleapis.com";

    /// <summary>Android notification channel the app creates.</summary>
    public string AndroidChannelId { get; set; } = "yardsyncs";
}

public sealed class FcmPushSender(HttpClient http, FcmOptions options, TimeProvider clock) : INotificationSender
{
    private const string Scope = "https://www.googleapis.com/auth/firebase.messaging";
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _accessToken;
    private DateTimeOffset _accessTokenUntil;

    public NotificationChannel Channel => NotificationChannel.Push;
    public string Provider => "FCM";

    public async Task<string?> SendAsync(string recipient, RenderedMessage message, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(options.ProjectId) || string.IsNullOrEmpty(options.ClientEmail) || string.IsNullOrEmpty(options.PrivateKey))
            throw new NotificationSendException("FCM is not configured (ProjectId / ClientEmail / PrivateKey).", permanent: true);

        var accessToken = await AccessTokenAsync(ct);
        var payload = new
        {
            message = new
            {
                token = recipient,
                notification = new { title = message.Subject, body = message.Body },
                data = message.Data ?? new Dictionary<string, string>(),
                android = new { priority = "HIGH", notification = new { channel_id = options.AndroidChannelId, sound = "default" } },
                apns = new
                {
                    headers = new Dictionary<string, string> { ["apns-priority"] = "10" },
                    payload = new { aps = new { sound = "default" } },
                },
            },
        };

        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"{options.BaseUrl.TrimEnd('/')}/v1/projects/{Uri.EscapeDataString(options.ProjectId)}/messages:send")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var (status, body) = await ProviderHttp.SendAsync(http, request, "FCM", ct);
        if ((int)status is >= 200 and < 300)
        {
            using var ok = ProviderHttp.ParseJson(body);
            return ok?.RootElement.TryGetProperty("name", out var name) == true ? name.GetString() : null;
        }

        var error = Error(body);
        if (status == HttpStatusCode.Unauthorized)
        {
            _accessToken = null; // signed out early: sign in again next time
            throw new NotificationSendException($"FCM refused the sign-in: {error.Message}", permanent: false);
        }
        // The app was removed or the token rotated: this phone can no longer be reached by push.
        if (status == HttpStatusCode.NotFound || error.Code is "UNREGISTERED" || (status == HttpStatusCode.BadRequest && error.Code is "INVALID_ARGUMENT"))
            throw new NotificationSendException($"FCM: the app token is no longer valid ({error.Code ?? ((int)status).ToString()}).", permanent: true, invalidRecipient: true);
        if (status == HttpStatusCode.Forbidden || error.Code is "SENDER_ID_MISMATCH" or "THIRD_PARTY_AUTH_ERROR")
            throw new NotificationSendException($"FCM configuration problem: {error.Message}", permanent: true);
        throw new NotificationSendException($"FCM error {(int)status}: {error.Message}", permanent: (int)status is >= 400 and < 500 and not 429);
    }

    /// <summary>An OAuth access token for the service account, reused until five minutes before it expires.</summary>
    private async Task<string> AccessTokenAsync(CancellationToken ct)
    {
        await _tokenLock.WaitAsync(ct);
        try
        {
            var now = clock.GetUtcNow();
            if (_accessToken is not null && now < _accessTokenUntil)
                return _accessToken;

            using var request = new HttpRequestMessage(HttpMethod.Post, options.TokenUri)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
                    ["assertion"] = SignedAssertion(now),
                }),
            };
            var (status, body) = await ProviderHttp.SendAsync(http, request, "FCM sign-in", ct);
            using var json = ProviderHttp.ParseJson(body);
            if ((int)status is < 200 or >= 300 || json?.RootElement.TryGetProperty("access_token", out var token) != true)
                throw new NotificationSendException($"FCM sign-in failed ({(int)status}).", permanent: (int)status is 400 or 401 or 403);

            var seconds = json.RootElement.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var s) ? s : 3600;
            _accessToken = token.GetString()!;
            _accessTokenUntil = now.AddSeconds(Math.Max(60, seconds - 300));
            return _accessToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    /// <summary>The service-account JWT (RS256) exchanged for an access token.</summary>
    public string SignedAssertion(DateTimeOffset now)
    {
        static string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var header = B64(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT" }));
        var iat = now.ToUnixTimeSeconds();
        var claims = B64(JsonSerializer.SerializeToUtf8Bytes(new { iss = options.ClientEmail, scope = Scope, aud = options.TokenUri, iat, exp = iat + 3600 }));
        using var rsa = RSA.Create();
        try
        {
            rsa.ImportFromPem(options.PrivateKey.Replace("\\n", "\n"));
        }
        catch (ArgumentException ex)
        {
            throw new NotificationSendException("FCM private key is not a valid PEM key.", permanent: true, ex);
        }
        var signature = rsa.SignData(Encoding.ASCII.GetBytes($"{header}.{claims}"), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{header}.{claims}.{B64(signature)}";
    }

    private static (string? Code, string Message) Error(string body)
    {
        using var json = ProviderHttp.ParseJson(body);
        if (json is null || !json.RootElement.TryGetProperty("error", out var error))
            return (null, body.Length > 200 ? body[..200] : body);
        var message = error.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
        string? code = error.TryGetProperty("status", out var st) ? st.GetString() : null;
        // FCM puts its own error code in details[].errorCode (e.g. UNREGISTERED).
        if (error.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Array)
            foreach (var d in details.EnumerateArray())
                if (d.TryGetProperty("errorCode", out var ec))
                    code = ec.GetString();
        return (code, message);
    }
}
