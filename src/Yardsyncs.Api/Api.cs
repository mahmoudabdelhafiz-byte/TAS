using Yardsyncs.Application.Auth;
using Yardsyncs.Application.Booking;
using Yardsyncs.Application.Capacity;
using Yardsyncs.Infrastructure.Auth;

namespace Yardsyncs.Api;

// ---------------------------------------------------------------- request / response bodies

public sealed record LoginBody(string Email, string Password);

/// <param name="RefreshToken">For API clients; the portal leaves it out and sends its cookie (see <see cref="PortalSession"/>).</param>
public sealed record RefreshBody(string? RefreshToken = null);

/// <summary>
/// The portal's session: the refresh token lives in an HttpOnly cookie the page's scripts can't read, and the access
/// token only in memory. A request asks for this with the X-Yardsyncs-Session: cookie header (a custom header, so other
/// sites can't send it); API clients keep using the tokens in the body.
/// </summary>
public static class PortalSession
{
    public const string Header = "X-Yardsyncs-Session";
    public const string Cookie = "ys_refresh";

    public static bool Wanted(HttpContext http) => string.Equals(http.Request.Headers[Header], "cookie", StringComparison.OrdinalIgnoreCase);

    /// <summary>The refresh token: from the body, or (portal) from the cookie.</summary>
    public static string? Token(HttpContext http, RefreshBody? body) =>
        !string.IsNullOrWhiteSpace(body?.RefreshToken) ? body.RefreshToken
        : Wanted(http) ? http.Request.Cookies[Cookie] : null;

    /// <summary>The answer to a sign-in or refresh: for the portal the refresh token goes into the cookie, not the body.</summary>
    public static IResult Issue(HttpContext http, AuthResult result)
    {
        var tokens = TokenResponse.From(result);
        if (!Wanted(http))
            return Results.Ok(tokens);
        http.Response.Cookies.Append(Cookie, tokens.RefreshToken, Options(http, tokens.RefreshTokenExpiresAt));
        return Results.Ok(tokens with { RefreshToken = "" });
    }

    public static void End(HttpContext http)
    {
        if (Wanted(http) || http.Request.Cookies.ContainsKey(Cookie))
            http.Response.Cookies.Delete(Cookie, Options(http, null));
    }

    private static CookieOptions Options(HttpContext http, DateTimeOffset? expires) => new()
    {
        HttpOnly = true,
        Secure = http.Request.IsHttps,
        SameSite = SameSiteMode.Strict,
        Path = "/api/auth",
        Expires = expires,
        IsEssential = true,
    };
}

public sealed record ChangePasswordBody(string CurrentPassword, string NewPassword);

public sealed record ForgotPasswordBody(string? Email);

public sealed record FollowBody(IReadOnlyList<string>? Containers);

public sealed record SetPasswordBody(string? Token, string? NewPassword);

/// <param name="CompanyId">Ignored for company users (taken from their account); required for terminal staff booking on behalf of a company.</param>
public sealed record CreateAppointmentBody(
    long SlotId,
    IReadOnlyList<BookingItemRequest>? Items,
    int? CompanyId = null,
    int? TruckId = null,
    int? DriverId = null);

public sealed record CancelBody(string? Reason);

public sealed record EraseBody(string? Reason);

public sealed record RescheduleBody(long SlotId);

public sealed record AssignBody(int? TruckId, int? DriverId);

public sealed record NotificationView(
    string Template,
    Yardsyncs.Domain.NotificationChannel Channel,
    string Language,
    Yardsyncs.Domain.NotificationStatus Status,
    string? Recipient,
    int Attempts,
    string? LastError,
    DateTimeOffset CreatedAt,
    DateTimeOffset? SentAt);

public sealed record TokenResponse(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt,
    UserProfile User)
{
    public static TokenResponse From(AuthResult result) =>
        new(result.Tokens!.AccessToken, result.Tokens.AccessTokenExpiresAt, result.Tokens.RefreshToken, result.Tokens.RefreshTokenExpiresAt, result.User!);
}
