using System.Text.Json.Serialization;
using Tas.Application.Booking;
using Tas.Infrastructure;
using Tas.Infrastructure.Booking;
using Tas.Infrastructure.Capacity;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddTasInfrastructure(builder.Configuration);
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();

app.UseExceptionHandler();
if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

var terminal = app.MapGroup("/api/terminals/{terminalId:int}");

// ---------------------------------------------------------------- capacity

// Regenerate slots for a date range (local dates, inclusive) after changing templates or overrides.
terminal.MapPost("/slots/generate", async (
    int terminalId, DateOnly from, DateOnly to, SlotGenerationService service, CancellationToken ct) =>
{
    if (to < from)
        return Results.Problem("'to' must be on or after 'from'.", statusCode: StatusCodes.Status400BadRequest);
    if (to.DayNumber - from.DayNumber > 90)
        return Results.Problem("The range can be at most 90 days.", statusCode: StatusCodes.Status400BadRequest);

    try
    {
        return Results.Ok(await service.GenerateAsync(terminalId, from, to, ct));
    }
    catch (KeyNotFoundException ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status404NotFound);
    }
});

// Slots of one local day with how many more trucks of a category can still book each one.
terminal.MapGet("/availability", async (
    int terminalId, DateOnly date, string category, int? companyId, AvailabilityQueryService service, CancellationToken ct) =>
{
    try
    {
        return Results.Ok(await service.GetAsync(terminalId, date, category, companyId, ct));
    }
    catch (KeyNotFoundException ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status404NotFound);
    }
});

// ---------------------------------------------------------------- appointments
// NOTE: userId in the body is temporary — it will come from authentication.

var appointments = terminal.MapGroup("/appointments");

appointments.MapPost("/", async (int terminalId, BookingRequest request, BookingService service, CancellationToken ct) =>
    ToResult(await service.CreateAsync(terminalId, request, ct), $"/api/terminals/{terminalId}/appointments"));

appointments.MapGet("/{appointmentNo}", async (int terminalId, string appointmentNo, AppointmentQueryService service, CancellationToken ct) =>
{
    try
    {
        var appointment = await service.GetAsync(terminalId, appointmentNo, ct);
        return appointment is null
            ? Results.Problem($"Appointment {appointmentNo} not found.", statusCode: StatusCodes.Status404NotFound)
            : Results.Ok(appointment);
    }
    catch (KeyNotFoundException ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status404NotFound);
    }
});

appointments.MapGet("/", async (int terminalId, DateOnly date, int? companyId, AppointmentQueryService service, CancellationToken ct) =>
{
    try
    {
        return Results.Ok(await service.ListAsync(terminalId, date, companyId, ct));
    }
    catch (KeyNotFoundException ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status404NotFound);
    }
});

appointments.MapPost("/{appointmentNo}/cancel", async (
    int terminalId, string appointmentNo, CancelRequest body, BookingService service, CancellationToken ct) =>
    ToResult(await service.CancelAsync(terminalId, appointmentNo, body.UserId, body.Reason, ct)));

appointments.MapPost("/{appointmentNo}/reschedule", async (
    int terminalId, string appointmentNo, RescheduleRequest body, BookingService service, CancellationToken ct) =>
    ToResult(await service.RescheduleAsync(terminalId, appointmentNo, body.UserId, body.SlotId, ct)));

appointments.MapPut("/{appointmentNo}/assignment", async (
    int terminalId, string appointmentNo, AssignRequest body, BookingService service, CancellationToken ct) =>
    ToResult(await service.AssignAsync(terminalId, appointmentNo, body.UserId, body.TruckId, body.DriverId, ct)));

// ---------------------------------------------------------------- development helpers

if (app.Environment.IsDevelopment())
{
    app.MapPost("/api/dev/seed", async (DemoDataSeeder seeder, CancellationToken ct) => Results.Ok(await seeder.SeedAsync(ct)));
    app.MapPost("/api/dev/no-shows", async (NoShowService service, CancellationToken ct) =>
        Results.Ok(new { marked = await service.MarkNoShowsAsync(ct) }));
}

app.Run();

static IResult ToResult(BookingOutcome outcome, string? createdBase = null)
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

public sealed record CancelRequest(int UserId, string? Reason);

public sealed record RescheduleRequest(int UserId, long SlotId);

public sealed record AssignRequest(int UserId, int? TruckId, int? DriverId);

public partial class Program
{
}
