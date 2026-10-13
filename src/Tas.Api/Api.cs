using Tas.Application.Auth;
using Tas.Application.Booking;
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
            _ => Results.Problem(detail, statusCode: StatusCodes.Status409Conflict, title: "Conflict", extensions: extensions),
        };
    }
}
