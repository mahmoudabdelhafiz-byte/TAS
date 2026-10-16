using Yardsyncs.Application.Auth;
using Yardsyncs.Application.Booking;
using Yardsyncs.Infrastructure.Auth;
using Yardsyncs.Infrastructure.DriverApp;

namespace Yardsyncs.Api;

public sealed record DriverLoginStartBody(string? Mobile, string? InstallId);

public sealed record PositionBody(double Latitude, double Longitude, double? Speed);

public sealed record DriverLoginVerifyBody(string? Mobile, string? InstallId, string? Code, string? Platform, string? AppVersion);

/// <summary>The driver app: sign in with a code by SMS, my appointments, start trip. Driver tokens only work here.</summary>
public static class DriverEndpoints
{
    public static WebApplication MapDriverApp(this WebApplication app)
    {
        var login = app.MapGroup("/api/driver/login").RequireRateLimiting("auth");

        // Same answer whether or not the number belongs to a driver.
        login.MapPost("/start", async (DriverLoginStartBody body, DriverLoginService service, CancellationToken ct) =>
        {
            var result = await service.StartAsync(body.Mobile, body.InstallId, ct);
            return result.Status switch
            {
                DriverLoginStatus.Ok => Results.Accepted(value: new { sent = true, expiresInSeconds = 300, testCode = result.TestCode }),
                DriverLoginStatus.TooMany => YardsyncsApi.Problem(StatusCodes.Status429TooManyRequests, result.Error!),
                _ => YardsyncsApi.Problem(StatusCodes.Status400BadRequest, result.Error!),
            };
        });

        login.MapPost("/verify", async (DriverLoginVerifyBody body, DriverLoginService service, CancellationToken ct) =>
        {
            var result = await service.VerifyAsync(body.Mobile, body.InstallId, body.Code, body.Platform, body.AppVersion, ct);
            return result.Status == DriverLoginStatus.Ok
                ? Results.Ok(result.Session)
                : YardsyncsApi.Problem(StatusCodes.Status401Unauthorized, result.Error!);
        });

        var driver = app.MapGroup("/api/driver").RequireAuthorization(DriverAuth.Policy);

        driver.MapPost("/logout", async (HttpContext http, DriverLoginService service, CancellationToken ct) =>
        {
            await service.LogoutAsync(Me(http).DeviceId, ct);
            return Results.NoContent();
        });

        // The app's push address (Firebase token); null signs this phone out of push.
        driver.MapPut("/push-token", async (PushTokenBody body, HttpContext http, DriverLoginService service, CancellationToken ct) =>
            await service.SetPushTokenAsync(Me(http).DeviceId, body.Token, ct) is { } error
                ? YardsyncsApi.Problem(StatusCodes.Status400BadRequest, error)
                : Results.NoContent());

        driver.MapGet("/me", async (HttpContext http, DriverAppService service, CancellationToken ct) =>
            await service.ProfileAsync(Me(http), ct) is { } profile ? Results.Ok(profile) : Results.Unauthorized());

        driver.MapGet("/appointments", async (HttpContext http, DriverAppService service, CancellationToken ct) =>
            Results.Ok(await service.ListAsync(Me(http), ct)));

        driver.MapGet("/appointments/{appointmentNo}", async (string appointmentNo, HttpContext http, DriverAppService service, CancellationToken ct) =>
            await service.GetAsync(Me(http), appointmentNo, ct) is { } view
                ? Results.Ok(view)
                : YardsyncsApi.Problem(StatusCodes.Status404NotFound, $"Appointment {appointmentNo} not found."));

        // The truck is leaving: re-check now; refused (422) while anything is red.
        driver.MapPost("/appointments/{appointmentNo}/start-trip", async (string appointmentNo, HttpContext http, DriverAppService service, CancellationToken ct) =>
        {
            var (outcome, view) = await service.StartTripAsync(Me(http), appointmentNo, ct);
            return outcome.Kind == BookingOutcomeKind.Updated && view is not null ? Results.Ok(view) : YardsyncsApi.ToResult(outcome);
        });

        // The secret behind the rotating gate QR (the app makes the codes offline).
        driver.MapGet("/gate-pass", async (HttpContext http, GatePassService service, CancellationToken ct) =>
            Results.Ok(await service.IssueAsync(Me(http), ct)));

        // Position on the way (arrival estimate, pre-gate check-in) and inside the terminal (arrival at the container's block).
        driver.MapPost("/appointments/{appointmentNo}/position", async (
            string appointmentNo, PositionBody body, HttpContext http, TripTrackingService service, CancellationToken ct) =>
        {
            var (update, error, notFound) = await service.ReportAsync(Me(http), appointmentNo, body.Latitude, body.Longitude, body.Speed, ct);
            return update is not null
                ? Results.Ok(update)
                : YardsyncsApi.Problem(notFound ? StatusCodes.Status404NotFound : StatusCodes.Status409Conflict, error!);
        });

        // Directions in the yard after gate-in: stops in order with routes along the terminal roads, and the map.
        driver.MapGet("/appointments/{appointmentNo}/yard", async (
            string appointmentNo, double? lat, double? lon, HttpContext http, YardGuidanceService service, CancellationToken ct) =>
        {
            var (guidance, error, notFound) = await service.ForDriverAsync(Me(http), appointmentNo, lat, lon, ct);
            return guidance is not null
                ? Results.Ok(guidance)
                : YardsyncsApi.Problem(notFound ? StatusCodes.Status404NotFound : StatusCodes.Status409Conflict, error!);
        });

        // "Done here" at a stop: the next stop becomes current; the terminal's systems are told.
        driver.MapPost("/appointments/{appointmentNo}/stops/{sequence:int}/done", async (
            string appointmentNo, int sequence, HttpContext http, YardGuidanceService service, CancellationToken ct) =>
        {
            var (error, notFound) = await service.CompleteStopAsync(Me(http), appointmentNo, sequence, ct);
            return error is null
                ? Results.NoContent()
                : YardsyncsApi.Problem(notFound ? StatusCodes.Status404NotFound : StatusCodes.Status409Conflict, error);
        });

        // Events sent (or kept) for the terminal's own systems, newest first: terminal staff.
        app.MapGet("/api/terminals/{terminalId:int}/integration-events", async (
            int terminalId, int? limit, HttpContext http, Yardsyncs.Infrastructure.Integration.IntegrationDispatcher service, CancellationToken ct) =>
            AccessRules.CanViewCapacity(YardsyncsApi.User(http)!, terminalId)
                ? Results.Ok(await service.ListAsync(terminalId, limit ?? 100, ct))
                : YardsyncsApi.Forbidden("Only terminal staff of this terminal can see its integration events."))
            .RequireAuthorization();

        // The terminal map (areas and roads): terminal staff read it, terminal admins draw the roads.
        var yardMap = app.MapGroup("/api/terminals/{terminalId:int}/yard-map").RequireAuthorization();
        yardMap.MapGet("/", async (int terminalId, HttpContext http, YardGuidanceService service, CancellationToken ct) =>
            AccessRules.CanViewCapacity(YardsyncsApi.User(http)!, terminalId)
                ? Results.Ok(await service.MapViewAsync(terminalId, ct))
                : YardsyncsApi.Forbidden("Only terminal staff of this terminal can see its map."));
        yardMap.MapGet("/roads", async (int terminalId, HttpContext http, YardGuidanceService service, CancellationToken ct) =>
            AccessRules.CanViewCapacity(YardsyncsApi.User(http)!, terminalId)
                ? Results.Content(await service.RoadsAsync(terminalId, ct), "application/json")
                : YardsyncsApi.Forbidden("Only terminal staff of this terminal can see its map."));
        yardMap.MapPut("/roads", async (int terminalId, RoadsInput body, HttpContext http, YardGuidanceService service, CancellationToken ct) =>
        {
            var user = YardsyncsApi.User(http)!;
            if (!AccessRules.CanManageCategories(user, terminalId))
                return YardsyncsApi.Forbidden("Only terminal admins of this terminal can change the roads.");
            var errors = await service.SaveRoadsAsync(terminalId, user.UserId, body.Roads, ct);
            return errors.Count == 0
                ? Results.NoContent()
                : YardsyncsApi.Problem(StatusCodes.Status400BadRequest, string.Join(" ", errors), new Dictionary<string, object?> { ["errors"] = errors });
        });

        // Terminal areas (approach, pre-gate, gate…): terminal staff read, terminal admins draw.
        var areas = app.MapGroup("/api/terminals/{terminalId:int}/geofences").RequireAuthorization();
        areas.MapGet("/", async (int terminalId, HttpContext http, TripTrackingService service, CancellationToken ct) =>
            AccessRules.CanViewCapacity(YardsyncsApi.User(http)!, terminalId)
                ? Results.Ok(await service.ListAsync(terminalId, ct))
                : YardsyncsApi.Forbidden("Only terminal staff of this terminal can see its areas."));
        areas.MapPost("/", async (int terminalId, GeofenceInput body, HttpContext http, TripTrackingService service, CancellationToken ct) =>
        {
            if (!AccessRules.CanManageCategories(YardsyncsApi.User(http)!, terminalId))
                return YardsyncsApi.Forbidden("Only terminal admins of this terminal can draw areas.");
            var (id, errors) = await service.CreateAsync(terminalId, body, ct);
            return id is null
                ? YardsyncsApi.Problem(StatusCodes.Status400BadRequest, string.Join(" ", errors), new Dictionary<string, object?> { ["errors"] = errors })
                : Results.Created($"/api/terminals/{terminalId}/geofences/{id}", new { geofenceId = id });
        });
        areas.MapPut("/{geofenceId:int}", async (int terminalId, int geofenceId, GeofenceInput body, HttpContext http, TripTrackingService service, CancellationToken ct) =>
        {
            if (!AccessRules.CanManageCategories(YardsyncsApi.User(http)!, terminalId))
                return YardsyncsApi.Forbidden("Only terminal admins of this terminal can change areas.");
            var (notFound, errors) = await service.UpdateAsync(terminalId, geofenceId, body, ct);
            if (notFound)
                return YardsyncsApi.Problem(StatusCodes.Status404NotFound, $"Area {geofenceId} not found.");
            return errors.Count == 0
                ? Results.NoContent()
                : YardsyncsApi.Problem(StatusCodes.Status400BadRequest, string.Join(" ", errors), new Dictionary<string, object?> { ["errors"] = errors });
        });
        areas.MapPost("/{geofenceId:int}/deactivate", async (int terminalId, int geofenceId, HttpContext http, TripTrackingService service, CancellationToken ct) =>
        {
            if (!AccessRules.CanManageCategories(YardsyncsApi.User(http)!, terminalId))
                return YardsyncsApi.Forbidden("Only terminal admins of this terminal can change areas.");
            return await service.DeactivateAsync(terminalId, geofenceId, ct) > 0
                ? Results.NoContent()
                : YardsyncsApi.Problem(StatusCodes.Status404NotFound, $"Area {geofenceId} not found.");
        });

        return app;
    }

    private static CurrentDriver Me(HttpContext http) => CurrentDriver.FromClaims(http.User)!;
}

public sealed record PushTokenBody(string? Token);
