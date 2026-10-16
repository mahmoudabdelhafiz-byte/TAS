using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Tas.Api;
using Tas.Application.Auth;
using Tas.Application.Booking;
using Tas.Infrastructure;
using Tas.Infrastructure.Auth;
using Tas.Infrastructure.Booking;
using Tas.Infrastructure.Capacity;
using Tas.Infrastructure.Ops;
using Tas.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

// One JSON object per log line (Logging:Json), for log collectors in containers and the cloud.
if (builder.Configuration.GetValue("Logging:Json", false))
{
    builder.Logging.ClearProviders();
    builder.Logging.AddJsonConsole(o =>
    {
        o.UseUtcTimestamp = true;
        o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ ";
    });
}

// Behind a reverse proxy (Caddy, nginx, a cloud load balancer): take the client address and https from its headers,
// so rate limits apply per client and links / HSTS see https. Only when the app is reachable through the proxy alone.
if (builder.Configuration.GetValue("Proxy:TrustForwardedHeaders", false))
{
    builder.Services.Configure<ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
        o.KnownIPNetworks.Clear();
        o.KnownProxies.Clear();
    });
}

builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database", tags: ["ready"]);

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
    // AI assistant: questions per minute per signed-in user (Assistant:PerMinute, default 10); the daily limit is in the service.
    o.AddPolicy("assistant", ctx => RateLimitPartition.GetFixedWindowLimiter(
        TasApi.User(ctx)?.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = ctx.RequestServices.GetRequiredService<IConfiguration>().GetValue("Assistant:PerMinute", 10),
            Window = TimeSpan.FromMinutes(1),
        }));
});

var app = builder.Build();

// Language packs (Languages:Path): more languages, or corrections to the built-in ones, without a new version.
// A pack with a mistake stops the start, with what is wrong.
Tas.Application.Languages.LanguageCatalog.Use(Tas.Infrastructure.LanguagePackLoader.Load(Tas.Infrastructure.LanguagePackLoader.FolderFor(app.Configuration)));

// Test-only switches must never reach a real server: refuse to start rather than run unsafely.
if (SecurityChecks.Unsafe(app.Configuration, app.Environment) is { Count: > 0 } unsafeSettings)
    throw new InvalidOperationException("Unsafe settings for " + app.Environment.EnvironmentName + ": " + string.Join(" ", unsafeSettings));

// `dotnet Tas.Api.dll migrate`: create or upgrade the database, then exit (for upgrades run by hand or a deploy job).
if (args.Contains("migrate", StringComparer.OrdinalIgnoreCase))
{
    foreach (var line in await app.Services.GetRequiredService<DatabaseMigrator>().MigrateAsync())
        Console.WriteLine(line);
    Console.WriteLine($"Database is at version {DatabaseMigrator.Latest}.");
    return;
}

if (app.Configuration.GetValue("Proxy:TrustForwardedHeaders", false))
    app.UseForwardedHeaders();

app.UseExceptionHandler();

if (!app.Environment.IsDevelopment())
    app.UseHsts();

// Browser hardening for the portal and the API.
app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"] = "DENY";
    headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    // Map tiles come from the tile server set in index.html (any https host); styles include inline style attributes.
    headers["Content-Security-Policy"] =
        "default-src 'self'; img-src 'self' data: https:; style-src 'self' 'unsafe-inline'; script-src 'self'; " +
        "font-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
    await next();
});

// The web portal (web/, built into wwwroot by `npm run build`) is served from the same address as the API.
// It uses hash routes (/#/appointments), so only "/" needs index.html — no fallback route that could shadow /api.
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseAuthentication();
app.UseRateLimiter(); // after authentication, so limits can be per signed-in user
app.UseAuthorization();

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

// Create or upgrade the database on start (Database:MigrateOnStartup, default on); instances wait for each other.
if (app.Configuration.GetValue("Database:MigrateOnStartup", true))
    await app.Services.GetRequiredService<DatabaseMigrator>().MigrateAsync();

using (var scope = app.Services.CreateScope())
    await scope.ServiceProvider.GetRequiredService<AdminBootstrapper>().EnsureAsync();

// Health: "live" = the process answers (restart it if not); "ready" = the database answers and is up to date
// (send traffic only then). For load balancers, container platforms and monitoring.
app.MapGet("/health", () => Results.Ok(new { status = "ok", version = OpsStatusService.Version }));
app.MapGet("/health/live", () => Results.Ok(new { status = "ok", version = OpsStatusService.Version }));
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = c => c.Tags.Contains("ready"),
    ResponseWriter = async (context, report) =>
    {
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new
        {
            status = report.Status.ToString(),
            version = OpsStatusService.Version,
            checks = report.Entries.Select(e => new { name = e.Key, status = e.Value.Status.ToString(), detail = e.Value.Description }),
        });
    },
    ResultStatusCodes =
    {
        [HealthStatus.Healthy] = StatusCodes.Status200OK,
        [HealthStatus.Degraded] = StatusCodes.Status200OK,
        [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable,
    },
});

// For administrators and monitoring: version, database, message and event backlogs.
app.MapGet("/api/ops/status", async (HttpContext http, OpsStatusService service, CancellationToken ct) =>
{
    var user = TasApi.User(http)!;
    return user.IsSysAdmin || user.IsTerminalAdministrator
        ? Results.Ok(await service.StatusAsync(app.Environment.EnvironmentName, ct, user.IsSysAdmin ? null : user.WorksAt))
        : TasApi.Forbidden("Only administrators can see the system status.");
}).RequireAuthorization();

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

// Forgot password: the same answer whether or not the email has an account.
auth.MapPost("/forgot-password", async (ForgotPasswordBody body, PasswordLinkService service, HttpContext http, CancellationToken ct) =>
{
    var testToken = await service.RequestResetAsync(body.Email, TasApi.Origin(http), ct);
    return Results.Accepted(value: new { sent = true, testToken });
}).RequireRateLimiting("auth");

// The "set your password" page: who the link is for (404 when it doesn't work any more).
auth.MapGet("/password-link", async (string token, PasswordLinkService service, CancellationToken ct) =>
    await service.InfoAsync(token, ct) is { } info
        ? Results.Ok(info)
        : TasApi.Problem(StatusCodes.Status404NotFound, PasswordLinkService.BadLink)).RequireRateLimiting("auth");

auth.MapPost("/password-link", async (SetPasswordBody body, PasswordLinkService service, CancellationToken ct) =>
    await service.CompleteAsync(body.Token, body.NewPassword, ct) is { } error
        ? TasApi.Problem(StatusCodes.Status400BadRequest, error)
        : Results.NoContent()).RequireRateLimiting("auth");

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
    var result = await service.CreateAsync(TasApi.User(http)!, body, ct, TasApi.Origin(http));
    if (result.TestToken is not null)
        http.Response.Headers["X-Yardsyncs-Test-Token"] = result.TestToken;
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

// Book by BL / several containers: list the containers with their check results (books nothing).
terminal.MapPost("/containers/lookup", async (
    int terminalId, ContainerLookupInput body, HttpContext http, ContainerLookupService service, CancellationToken ct) =>
{
    var user = TasApi.User(http)!;
    return !AccessRules.CanAccessTerminal(user, terminalId)
        ? TasApi.Forbidden("You have no access to this terminal.")
        : TasApi.Admin(await service.LookupAsync(user, terminalId, body, ct));
});

// Track containers (no booking): status by BL or container numbers; follow containers to get an email when ready.
terminal.MapPost("/containers/track", async (int terminalId, TrackInput body, HttpContext http, ContainerTrackingService service, CancellationToken ct) =>
{
    var user = TasApi.User(http)!;
    return !AccessRules.CanAccessTerminal(user, terminalId)
        ? TasApi.Forbidden("You have no access to this terminal.")
        : TasApi.Admin(await service.TrackAsync(user, terminalId, body, ct));
});
terminal.MapGet("/containers/followed", async (int terminalId, HttpContext http, ContainerTrackingService service, CancellationToken ct) =>
{
    var user = TasApi.User(http)!;
    return !AccessRules.CanAccessTerminal(user, terminalId)
        ? TasApi.Forbidden("You have no access to this terminal.")
        : Results.Ok(await service.FollowedAsync(user, terminalId, ct));
});
terminal.MapPost("/containers/followed", async (int terminalId, FollowBody body, HttpContext http, ContainerTrackingService service, CancellationToken ct) =>
{
    var user = TasApi.User(http)!;
    return !AccessRules.CanAccessTerminal(user, terminalId)
        ? TasApi.Forbidden("You have no access to this terminal.")
        : TasApi.Admin(await service.FollowAsync(user, terminalId, body.Containers, ct));
});
terminal.MapDelete("/containers/followed/{containerNo}", async (int terminalId, string containerNo, HttpContext http, ContainerTrackingService service, CancellationToken ct) =>
{
    var user = TasApi.User(http)!;
    if (!AccessRules.CanAccessTerminal(user, terminalId))
        return TasApi.Forbidden("You have no access to this terminal.");
    return await service.UnfollowAsync(user, terminalId, containerNo, ct) > 0
        ? Results.NoContent()
        : TasApi.Problem(StatusCodes.Status404NotFound, $"You don't follow {containerNo}.");
});

// ================================================================ AI assistant

app.MapGet("/api/assistant", (Tas.Infrastructure.Assistant.AssistantService service) => Results.Ok(service.Info())).RequireAuthorization();

// A question with the conversation so far (kept by the browser); read-only look-ups with the user's permissions.
terminal.MapPost("/assistant", async (
    int terminalId, Tas.Infrastructure.Assistant.ChatInput body, HttpContext http, Tas.Infrastructure.Assistant.AssistantService service, CancellationToken ct) =>
{
    var result = await service.ChatAsync(TasApi.User(http)!, terminalId, body, ct);
    return result.Status switch
    {
        Tas.Infrastructure.Assistant.ChatStatus.Ok => Results.Ok(result.Answer),
        Tas.Infrastructure.Assistant.ChatStatus.Off => TasApi.Problem(StatusCodes.Status404NotFound, result.Error!),
        Tas.Infrastructure.Assistant.ChatStatus.Limit => TasApi.Problem(StatusCodes.Status429TooManyRequests, result.Error!),
        Tas.Infrastructure.Assistant.ChatStatus.Unavailable => TasApi.Problem(StatusCodes.Status503ServiceUnavailable, result.Error!),
        _ => TasApi.Problem(StatusCodes.Status400BadRequest, result.Error!),
    };
}).RequireRateLimiting("assistant");

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

// Bulk booking: upload a .csv or .xlsx of truck visits. dryRun (default true) checks everything and books nothing.
appointments.MapPost("/bulk", async (
    int terminalId, IFormFile? file, bool? dryRun, int? companyId, HttpContext http, BulkBookingService service, CancellationToken ct) =>
{
    var user = TasApi.User(http)!;
    if (!AccessRules.CanAccessTerminal(user, terminalId))
        return TasApi.Forbidden("You have no access to this terminal.");

    var (company, error) = AccessRules.ResolveCompany(user, companyId, required: true);
    if (error is not null)
        return user.IsCompanyUser ? TasApi.Forbidden(error) : TasApi.Problem(StatusCodes.Status400BadRequest, error);

    if (file is null || file.Length == 0)
        return TasApi.Problem(StatusCodes.Status400BadRequest, "Attach a .csv or .xlsx file in the 'file' field.");
    if (file.Length > BulkFiles.MaxUploadBytes)
        return TasApi.Problem(StatusCodes.Status413PayloadTooLarge, "The file is larger than 2 MB; split it.");

    IReadOnlyList<IReadOnlyList<string>> table;
    var name = file.FileName.ToLowerInvariant();
    try
    {
        await using var stream = file.OpenReadStream();
        if (name.EndsWith(".xlsx"))
            table = BulkFiles.ReadExcel(stream);
        else if (name.EndsWith(".csv") || name.EndsWith(".txt"))
            table = BulkFiles.ReadCsv(stream);
        else
            return TasApi.Problem(StatusCodes.Status400BadRequest, "Upload a .csv or .xlsx file.");
    }
    catch (InvalidDataException ex) when (ex.Message.StartsWith("The workbook is too large", StringComparison.Ordinal))
    {
        return TasApi.Problem(StatusCodes.Status413PayloadTooLarge, ex.Message);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        return TasApi.Problem(StatusCodes.Status400BadRequest, "The file could not be read. Save it as .xlsx or CSV UTF-8 and try again.");
    }

    try
    {
        return Results.Ok(await service.ImportAsync(terminalId, company!.Value, user.UserId, table, dryRun ?? true, ct));
    }
    catch (KeyNotFoundException ex)
    {
        return TasApi.Problem(StatusCodes.Status404NotFound, ex.Message);
    }
}).DisableAntiforgery().WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(BulkFiles.MaxUploadBytes + 64 * 1024));

appointments.MapGet("/bulk/template", (int terminalId, string? format, HttpContext http, TimeProvider clock) =>
{
    if (!AccessRules.CanAccessTerminal(TasApi.User(http)!, terminalId))
        return TasApi.Forbidden("You have no access to this terminal.");

    var tomorrow = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime).AddDays(1);
    return string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase)
        ? Results.File(BulkFiles.CsvTemplate(tomorrow), "text/csv", "tas-bulk-booking-template.csv")
        : Results.File(BulkFiles.ExcelTemplate(tomorrow), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "tas-bulk-booking-template.xlsx");
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

// Re-check the appointment against the TOS now (holds, releases, payment…) and refresh the traffic light.
appointments.MapPost("/{appointmentNo}/revalidate", async (
    int terminalId, string appointmentNo, HttpContext http, RevalidationService service, CancellationToken ct) =>
{
    var user = TasApi.User(http)!;
    return !AccessRules.CanAccessTerminal(user, terminalId)
        ? TasApi.Forbidden("You have no access to this terminal.")
        : TasApi.ToResult(await service.RevalidateByNumberAsync(terminalId, appointmentNo, user, ct));
});

// Messages sent (or queued) for an appointment — for the help desk and dispatchers.
appointments.MapGet("/{appointmentNo}/notifications", async (
    int terminalId, string appointmentNo, HttpContext http, TasDbContext db, CancellationToken ct) =>
{
    var user = TasApi.User(http)!;
    if (!AccessRules.CanAccessTerminal(user, terminalId))
        return TasApi.Forbidden("You have no access to this terminal.");

    var number = appointmentNo.Trim().ToUpperInvariant();
    var query = db.Appointments.AsNoTracking().Where(a => a.TerminalId == terminalId && a.AppointmentNo == number);
    if (user.IsCompanyUser)
        query = query.Where(a => a.CompanyId == user.CompanyId);
    var id = await query.Select(a => (long?)a.AppointmentId).SingleOrDefaultAsync(ct);
    if (id is null)
        return TasApi.Problem(StatusCodes.Status404NotFound, $"Appointment {number} not found.");

    var messages = await db.NotificationLogs.AsNoTracking()
        .Where(n => n.AppointmentId == id)
        .OrderBy(n => n.NotificationId)
        .Select(n => new NotificationView(n.TemplateCode, n.Channel, n.Language, n.Status, n.Recipient, n.Attempts, n.LastError, n.CreatedAt, n.SentAt))
        .ToListAsync(ct);
    return Results.Ok(messages);
});

// The truck is leaving: re-check now; refused (422) while anything is red, otherwise the appointment becomes EN_ROUTE.
appointments.MapPost("/{appointmentNo}/start-trip", async (
    int terminalId, string appointmentNo, HttpContext http, RevalidationService service, CancellationToken ct) =>
{
    var user = TasApi.User(http)!;
    return !AccessRules.CanAccessTerminal(user, terminalId)
        ? TasApi.Forbidden("You have no access to this terminal.")
        : TasApi.ToResult(await service.StartTripAsync(terminalId, appointmentNo, user, ct));
});

appointments.MapPut("/{appointmentNo}/assignment", async (
    int terminalId, string appointmentNo, AssignBody body, HttpContext http, BookingService service, CancellationToken ct) =>
    TasApi.ToResult(await service.AssignAsync(terminalId, appointmentNo, TasApi.User(http)!.UserId, body.TruckId, body.DriverId, ct)));

// ================================================================ master data

app.MapMasterData();
app.MapBranding();
app.MapOnboarding();
app.MapGate();
app.MapDriverApp();

// ================================================================ development helpers

if (app.Environment.IsDevelopment())
{
    app.MapPost("/api/dev/seed", async (DemoDataSeeder seeder, CancellationToken ct) => Results.Ok(await seeder.SeedAsync(ct)));
    // Load test data (loadtest/): many companies with admins and drivers already on the way.
    app.MapPost("/api/dev/load-seed", async (int? companies, int? driversPerCompany, LoadTestSeeder seeder, CancellationToken ct) =>
        Results.Ok(await seeder.SeedAsync(companies ?? 100, driversPerCompany ?? 2, ct)));
    app.MapPost("/api/dev/no-shows", async (NoShowService service, CancellationToken ct) =>
        Results.Ok(new { marked = await service.MarkNoShowsAsync(ct) }));
}

app.Run();

public partial class Program
{
}
