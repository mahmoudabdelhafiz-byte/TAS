using Tas.Application.Auth;
using Tas.Application.Booking;
using Tas.Infrastructure.Auth;
using Tas.Infrastructure.DriverApp;

namespace Tas.Api;

public sealed record DriverLoginStartBody(string? Mobile, string? InstallId);

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
                DriverLoginStatus.TooMany => TasApi.Problem(StatusCodes.Status429TooManyRequests, result.Error!),
                _ => TasApi.Problem(StatusCodes.Status400BadRequest, result.Error!),
            };
        });

        login.MapPost("/verify", async (DriverLoginVerifyBody body, DriverLoginService service, CancellationToken ct) =>
        {
            var result = await service.VerifyAsync(body.Mobile, body.InstallId, body.Code, body.Platform, body.AppVersion, ct);
            return result.Status == DriverLoginStatus.Ok
                ? Results.Ok(result.Session)
                : TasApi.Problem(StatusCodes.Status401Unauthorized, result.Error!);
        });

        var driver = app.MapGroup("/api/driver").RequireAuthorization(DriverAuth.Policy);

        driver.MapPost("/logout", async (HttpContext http, DriverLoginService service, CancellationToken ct) =>
        {
            await service.LogoutAsync(Me(http).DeviceId, ct);
            return Results.NoContent();
        });

        driver.MapGet("/me", async (HttpContext http, DriverAppService service, CancellationToken ct) =>
            await service.ProfileAsync(Me(http), ct) is { } profile ? Results.Ok(profile) : Results.Unauthorized());

        driver.MapGet("/appointments", async (HttpContext http, DriverAppService service, CancellationToken ct) =>
            Results.Ok(await service.ListAsync(Me(http), ct)));

        driver.MapGet("/appointments/{appointmentNo}", async (string appointmentNo, HttpContext http, DriverAppService service, CancellationToken ct) =>
            await service.GetAsync(Me(http), appointmentNo, ct) is { } view
                ? Results.Ok(view)
                : TasApi.Problem(StatusCodes.Status404NotFound, $"Appointment {appointmentNo} not found."));

        // The truck is leaving: re-check now; refused (422) while anything is red.
        driver.MapPost("/appointments/{appointmentNo}/start-trip", async (string appointmentNo, HttpContext http, DriverAppService service, CancellationToken ct) =>
        {
            var (outcome, view) = await service.StartTripAsync(Me(http), appointmentNo, ct);
            return outcome.Kind == BookingOutcomeKind.Updated && view is not null ? Results.Ok(view) : TasApi.ToResult(outcome);
        });

        return app;
    }

    private static CurrentDriver Me(HttpContext http) => CurrentDriver.FromClaims(http.User)!;
}
