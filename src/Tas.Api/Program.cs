using Tas.Infrastructure;
using Tas.Infrastructure.Capacity;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddTasInfrastructure(builder.Configuration);
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();

var app = builder.Build();

app.UseExceptionHandler();
if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

var terminal = app.MapGroup("/api/terminals/{terminalId:int}");

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

app.Run();

public partial class Program
{
}
