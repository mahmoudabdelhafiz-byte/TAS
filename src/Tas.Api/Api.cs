using Tas.Application.Auth;
using Tas.Application.Booking;
using Tas.Application.Capacity;
using Tas.Infrastructure.Auth;

namespace Tas.Api;

// ---------------------------------------------------------------- request / response bodies

public sealed record LoginBody(string Email, string Password);

public sealed record RefreshBody(string RefreshToken);

public sealed record ChangePasswordBody(string CurrentPassword, string NewPassword);

/// <param name="CompanyId">Ignored for company users (taken from their account); required for terminal staff booking on behalf of a company.</param>
public sealed record CreateAppointmentBody(
    long SlotId,
    IReadOnlyList<BookingItemRequest>? Items,
    int? CompanyId = null,
    int? TruckId = null,
    int? DriverId = null);

public sealed record CancelBody(string? Reason);

public sealed record ForgotPasswordBody(string? Email);

public sealed record LanguageBody(string? Language);

public sealed record PasswordLinkBody(string? Token);

public sealed record ResetWithLinkBody(string? Token, string? NewPassword);

/// <param name="Invited">True when created without a password: an invitation email with a link was queued.</param>
public sealed record CreatedUserResponse(
    int UserId, string Email, string FullName, Tas.Domain.Parties.UserRole Role, int? CompanyId, string? CompanyName,
    int? TerminalId, string PreferredLanguage, bool Invited)
{
    public static CreatedUserResponse From(UserProfile p, bool invited) =>
        new(p.UserId, p.Email, p.FullName, p.Role, p.CompanyId, p.CompanyName, p.TerminalId, p.PreferredLanguage, invited);
}

public sealed record RescheduleBody(long SlotId);

public sealed record AssignBody(int? TruckId, int? DriverId);

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

// ---------------------------------------------------------------- helpers

internal static class TasApi
{
    /// <summary>The signed-in user. Endpoints that call this require authorization, so it is set.</summary>
    public static CurrentUser? User(HttpContext http) => CurrentUser.FromClaims(http.User);

    public static IResult Problem(int status, string detail, IDictionary<string, object?>? extensions = null) =>
        Results.Problem(detail, statusCode: status, extensions: extensions);

    public static IResult Forbidden(string detail) => Problem(StatusCodes.Status403Forbidden, detail);

    /// <summary>Maps a configuration change result: 200/201 with { data, regeneratedSlots }, or a problem response.</summary>
    public static IResult Admin<T>(AdminResult<T> result, Func<T, string>? location = null)
    {
        var extensions = new Dictionary<string, object?> { ["errors"] = result.Errors };
        var detail = string.Join(" ", result.Errors);

        return result.Status switch
        {
            AdminStatus.Ok => Results.Ok(new AdminResponse<T>(result.Value!, result.Regenerated)),
            AdminStatus.Created => Results.Created(location?.Invoke(result.Value!), new AdminResponse<T>(result.Value!, result.Regenerated)),
            AdminStatus.NotFound => Problem(StatusCodes.Status404NotFound, detail, extensions),
            AdminStatus.Conflict => Problem(StatusCodes.Status409Conflict, detail, extensions),
            AdminStatus.Forbidden => Problem(StatusCodes.Status403Forbidden, detail, extensions),
            _ => Problem(StatusCodes.Status400BadRequest, detail, extensions),
        };
    }

    public static IResult ToResult(BookingOutcome outcome, string? createdBase = null)
    {
        var extensions = new Dictionary<string, object?> { ["errors"] = outcome.Errors };
        if (outcome.Checks.Count > 0)
            extensions["checks"] = outcome.Checks;
        var detail = string.Join(" ", outcome.Errors);

        return outcome.Kind switch
        {
            BookingOutcomeKind.Created => Results.Created($"{createdBase}/{outcome.Appointment!.AppointmentNo}", outcome.Appointment),
            BookingOutcomeKind.Updated => Results.Ok(outcome.Appointment),
            BookingOutcomeKind.Invalid => Results.Problem(detail, statusCode: StatusCodes.Status400BadRequest, title: "Invalid request", extensions: extensions),
            BookingOutcomeKind.NotFound => Results.Problem(detail, statusCode: StatusCodes.Status404NotFound, title: "Not found", extensions: extensions),
            BookingOutcomeKind.Rejected => Results.Problem(detail, statusCode: StatusCodes.Status422UnprocessableEntity, title: "Not cleared for booking", extensions: extensions),
            BookingOutcomeKind.NoCapacity => Results.Problem(detail, statusCode: StatusCodes.Status409Conflict, title: "No capacity", extensions: extensions),
            BookingOutcomeKind.Valid => Results.Ok(new { valid = true, checks = outcome.Checks }),
            _ => Results.Problem(detail, statusCode: StatusCodes.Status409Conflict, title: "Conflict", extensions: extensions),
        };
    }
}
