using Microsoft.AspNetCore.Http;
using Yardsyncs.Application.Auth;
using Yardsyncs.Application.Booking;
using Yardsyncs.Application.Capacity;
using Yardsyncs.Application.Languages;

// The HTTP helpers every Yardsyncs module's endpoints share (core and Autogate): the signed-in user, problem
// responses, and the { data, regeneratedSlots } envelope of configuration changes.
namespace Yardsyncs.Api;

/// <summary>A configuration change: the changed thing, and the slots regenerated because of it (if any).</summary>
public sealed record AdminResponse<T>(T Data, object? RegeneratedSlots);

// ---------------------------------------------------------------- helpers

public static class YardsyncsApi
{
    /// <summary>The signed-in user. Endpoints that call this require authorization, so it is set.</summary>
    public static CurrentUser? User(HttpContext http) => CurrentUser.FromClaims(http.User);

    public static IResult Problem(int status, string detail, IDictionary<string, object?>? extensions = null) =>
        Results.Problem(detail, statusCode: status, extensions: extensions);

    public static IResult Forbidden(string detail) => Problem(StatusCodes.Status403Forbidden, detail);

    /// <summary>The address the request came to, for links in emails when PasswordLinks:PortalUrl isn't set.</summary>
    public static string Origin(HttpContext http) => $"{http.Request.Scheme}://{http.Request.Host}{http.Request.PathBase}";

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
        // Check results in the reader's language (the detail and errors are done with every problem response).
        var checks = outcome.Checks.Select(c => c with { Message = ServerText.TOrNull(c.Message) }).ToList();
        if (checks.Count > 0)
            extensions["checks"] = checks;
        var detail = string.Join(" ", outcome.Errors);

        return outcome.Kind switch
        {
            BookingOutcomeKind.Created => Results.Created($"{createdBase}/{outcome.Appointment!.AppointmentNo}", outcome.Appointment),
            BookingOutcomeKind.Updated => Results.Ok(outcome.Appointment),
            BookingOutcomeKind.Invalid => Results.Problem(detail, statusCode: StatusCodes.Status400BadRequest, title: "Invalid request", extensions: extensions),
            BookingOutcomeKind.NotFound => Results.Problem(detail, statusCode: StatusCodes.Status404NotFound, title: "Not found", extensions: extensions),
            BookingOutcomeKind.Rejected => Results.Problem(detail, statusCode: StatusCodes.Status422UnprocessableEntity, title: "Not cleared for booking", extensions: extensions),
            BookingOutcomeKind.NoCapacity => Results.Problem(detail, statusCode: StatusCodes.Status409Conflict, title: "No capacity", extensions: extensions),
            BookingOutcomeKind.Valid => Results.Ok(new { valid = true, checks }),
            _ => Results.Problem(detail, statusCode: StatusCodes.Status409Conflict, title: "Conflict", extensions: extensions),
        };
    }
}
