using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Tas.Api;
using Tas.Application.Auth;
using Tas.Application.Booking;
using Tas.Infrastructure;
using Tas.Infrastructure.Auth;
using Tas.Infrastructure.Booking;
using Tas.Infrastructure.Capacity;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddTasInfrastructure(builder.Configuration);
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// Login / refresh: N attempts per minute per client IP (RateLimit:AuthPerMinute, default 10).
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("auth", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = ctx.RequestServices.GetRequiredService<IConfiguration>().GetValue("RateLimit:AuthPerMinute", 10),
            Window = TimeSpan.FromMinutes(1),
        }));
});

var app = builder.Build();

app.UseExceptionHandler();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

using (var scope = app.Services.CreateScope())
    await scope.ServiceProvider.GetRequiredService<AdminBootstrapper>().EnsureAsync();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// ================================================================ auth

var auth = app.MapGroup("/api/auth");

auth.MapPost("/login", async (LoginBody body, AuthService service, HttpContext http, CancellationToken ct) =>
{
    var result = await service.LoginAsync(body.Email, body.Password, http.Connection.RemoteIpAddress?.ToString(), ct);
    return result.Status switch
    {
        AuthStatus.Ok => Results.Ok(TokenResponse.From(result)),
        AuthStatus.LockedOut => TasApi.Problem(StatusCodes.Status423Locked, result.Error!),
        _ => TasApi.Problem(StatusCodes.Status401Unauthorized, "Wrong email or password."),
    };
}).RequireRateLimiting("auth");

auth.MapPost("/refresh", async (RefreshBody body, AuthService service, HttpContext http, CancellationToken ct) =>
{
    var result = await service.RefreshAsync(body.RefreshToken, http.Connection.RemoteIpAddress?.ToString(), ct);
    return result.Status == AuthStatus.Ok
        ? Results.Ok(TokenResponse.From(result))
        : TasApi.Problem(StatusCodes.Status401Unauthorized, "Session expired. Please sign in again.");
}).RequireRateLimiting("auth");

auth.MapPost("/logout", async (RefreshBody body, AuthService service, CancellationToken ct) =>
{
    await service.LogoutAsync(body.RefreshToken, ct);
    return Results.NoContent();
});

auth.MapGet("/me", async (HttpContext http, AuthService service, CancellationToken ct) =>
{
    var user = TasApi.User(http);
    var profile = user is null ? null : await service.GetProfileAsync(user.UserId, ct);
    return profile is null ? Results.Unauthorized() : Results.Ok(profile);
}).RequireAuthorization();

auth.MapPost("/change-password", async (ChangePasswordBody body, HttpContext http, AuthService service, CancellationToken ct) =>
{
    var result = await service.ChangePasswordAsync(TasApi.User(http)!.UserId, body.CurrentPassword, body.NewPassword, ct);
    return result.Status switch
    {
        AuthStatus.Ok => Results.NoContent(),
        AuthStatus.Invalid => TasApi.Problem(StatusCodes.Status400BadRequest, result.Error!),
        _ => TasApi.Problem(StatusCodes.Status403Forbidden, result.Error ?? "Current password is wrong."),
    };
}).RequireAuthorization().RequireRateLimiting("auth");

// ================================================================ users

app.MapPost("/api/users", async (CreateUserRequest body, HttpContext http, UserAdminService service, CancellationToken ct) =>
{
    var result = await service.CreateAsync(TasApi.User(http)!, body, ct);
    return result.Status switch
    {
        UserAdminStatus.Created => Results.Created($"/api/users/{result.User!.UserId}", result.User),
        UserAdminStatus.Forbidden => TasApi.Problem(StatusCodes.Status403Forbidden, result.Error!),
        UserAdminStatus.Conflict => TasApi.Problem(StatusCodes.Status409Conflict, result.Error!),
        _ => TasApi.Problem(StatusCodes.Status400BadRequest, result.Error!),
    };
}).RequireAuthorization();

// ================================================================ terminal: capacity

var terminal = app.MapGroup("/api/terminals/{terminalId:int}").RequireAuthorization();

// Planner configuration: templates, overrides, reservations, release rules, settings, slot board, audit.
terminal.MapCapacityAdmin();

// Regenerate slots for a date range (local dates, inclusive) after changing templates or overrides.
terminal.MapPost("/slots/generate", async (
    int terminalId, DateOnly from, DateOnly to, HttpContext http, SlotGenerationService service, CancellationToken ct) =>
{
    if (!AccessRules.CanManageCapacity(TasApi.User(http)!, terminalId))
        return TasApi.Forbidden("Only planners and terminal admins of this terminal can regenerate slots.");
    if (to < from)
        return TasApi.Problem(StatusCodes.Status400BadRequest, "'to' must be on or after 'from'.");
    if (to.DayNumber - from.DayNumber > 90)
        return TasApi.Problem(StatusCodes.Status400BadRequest, "The range can be at most 90 days.");

    try
    {
        return Results.Ok(await service.GenerateAsync(terminalId, from, to, ct));
    }
    catch (KeyNotFoundException ex)
    {
        return TasApi.Problem(StatusCodes.Status404NotFound, ex.Message);
    }
});

// Slots of one local day with how many more trucks of a category can still book each one.
terminal.MapGet("/availability", async (
    int terminalId, DateOnly date, string category, int? companyId, HttpContext http, AvailabilityQueryService service, CancellationToken ct) =>
{
    var user = TasApi.User(http)!;
    if (!AccessRules.CanAccessTerminal(user, terminalId))
        return TasApi.Forbidden("You have no access to this terminal.");

    var (company, error) = AccessRules.ResolveCompany(user, companyId, required: false);
    if (error is not null)
        return TasApi.Forbidden(error);

    try
    {
        return Results.Ok(await service.GetAsync(terminalId, date, category, company, ct));
    }
    catch (KeyNotFoundException ex)
    {
        return TasApi.Problem(StatusCodes.Status404NotFound, ex.Message);
    }
});

// ================================================================ terminal: appointments

var appointments = terminal.MapGroup("/appointments");

appointments.MapPost("/", async (int terminalId, CreateAppointmentBody body, HttpContext http, BookingService service, CancellationToken ct) =>
{
    var user = TasApi.User(http)!;
    if (!AccessRules.CanAccessTerminal(user, terminalId))
        return TasApi.Forbidden("You have no access to this terminal.");

    var (companyId, error) = AccessRules.ResolveCompany(user, body.CompanyId, required: true);
    if (error is not null)
        return user.IsCompanyUser ? TasApi.Forbidden(error) : TasApi.Problem(StatusCodes.Status400BadRequest, error);

    var request = new BookingRequest(companyId!.Value, user.UserId, body.SlotId, body.TruckId, body.DriverId, body.Items ?? Array.Empty<BookingItemRequest>());
    return TasApi.ToResult(await service.CreateAsync(terminalId, request, ct), $"/api/terminals/{terminalId}/appointments");
});

appointments.MapGet("/{appointmentNo}", async (
    int terminalId, string appointmentNo, HttpContext http, AppointmentQueryService service, CancellationToken ct) =>
{
    var user = TasApi.User(http)!;
    if (!AccessRules.CanAccessTerminal(user, terminalId))
        return TasApi.Forbidden("You have no access to this terminal.");

    try
    {
        // Company users only ever see their own company's appointments; others look like "not found".
        var appointment = await service.GetAsync(terminalId, appointmentNo, user.IsCompanyUser ? user.CompanyId : null, ct);
        return appointment is null
            ? TasApi.Problem(StatusCodes.Status404NotFound, $"Appointment {appointmentNo} not found.")
            : Results.Ok(appointment);
    }
    catch (KeyNotFoundException ex)
    {
        return TasApi.Problem(StatusCodes.Status404NotFound, ex.Message);
    }
});

appointments.MapGet("/", async (
    int terminalId, DateOnly date, int? companyId, HttpContext http, AppointmentQueryService service, CancellationToken ct) =>
{
    var user = TasApi.User(http)!;
    if (!AccessRules.CanAccessTerminal(user, terminalId))
        return TasApi.Forbidden("You have no access to this terminal.");

    var (company, error) = AccessRules.ResolveCompany(user, companyId, required: false);
    if (error is not null)
        return TasApi.Forbidden(error);

    try
    {
        return Results.Ok(await service.ListAsync(terminalId, date, company, ct));
    }
    catch (KeyNotFoundException ex)
    {
        return TasApi.Problem(StatusCodes.Status404NotFound, ex.Message);
    }
});

appointments.MapPost("/{appointmentNo}/cancel", async (
    int terminalId, string appointmentNo, CancelBody? body, HttpContext http, BookingService service, CancellationToken ct) =>
    TasApi.ToResult(await service.CancelAsync(terminalId, appointmentNo, TasApi.User(http)!.UserId, body?.Reason, ct)));

appointments.MapPost("/{appointmentNo}/reschedule", async (
    int terminalId, string appointmentNo, RescheduleBody body, HttpContext http, BookingService service, CancellationToken ct) =>
    TasApi.ToResult(await service.RescheduleAsync(terminalId, appointmentNo, TasApi.User(http)!.UserId, body.SlotId, ct)));

appointments.MapPut("/{appointmentNo}/assignment", async (
    int terminalId, string appointmentNo, AssignBody body, HttpContext http, BookingService service, CancellationToken ct) =>
    TasApi.ToResult(await service.AssignAsync(terminalId, appointmentNo, TasApi.User(http)!.UserId, body.TruckId, body.DriverId, ct)));

// ================================================================ development helpers

if (app.Environment.IsDevelopment())
{
    app.MapPost("/api/dev/seed", async (DemoDataSeeder seeder, CancellationToken ct) => Results.Ok(await seeder.SeedAsync(ct)));
    app.MapPost("/api/dev/no-shows", async (NoShowService service, CancellationToken ct) =>
        Results.Ok(new { marked = await service.MarkNoShowsAsync(ct) }));
}

app.Run();

public partial class Program
{
}
