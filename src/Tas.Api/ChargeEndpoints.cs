using Tas.Application.Auth;
using Tas.Application.Capacity;
using Tas.Infrastructure.Booking;

namespace Tas.Api;

public sealed record WaiveBody(string? Reason);

/// <summary>Fees: the terminal's fee schedule, the charges report (JSON or CSV) and waiving a charge.</summary>
public static class ChargeEndpoints
{
    public static WebApplication MapCharges(this WebApplication app)
    {
        var terminal = app.MapGroup("/api/terminals/{terminalId:int}").RequireAuthorization();

        // Anyone who books at the terminal sees its fees before booking.
        terminal.MapGet("/fees", async (int terminalId, HttpContext http, ChargeService service, CancellationToken ct) =>
            AccessRules.CanAccessTerminal(TasApi.User(http)!, terminalId)
                ? Results.Ok(await service.FeesAsync(terminalId, ct))
                : TasApi.Forbidden("You cannot see this terminal."));

        terminal.MapGet("/charges", async (
            int terminalId, DateOnly from, DateOnly to, int? companyId, string? format, HttpContext http, ChargeService service, CancellationToken ct) =>
        {
            var result = await service.ReportAsync(TasApi.User(http)!, terminalId, from, to, companyId, ct);
            if (result.Status != AdminStatus.Ok)
                return TasApi.Admin(result);
            if (string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase))
                return Results.File(System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(ChargeService.Csv(result.Value!))).ToArray(),
                    "text/csv; charset=utf-8", $"charges-{from:yyyy-MM-dd}-{to:yyyy-MM-dd}.csv");
            return Results.Ok(result.Value);
        });

        terminal.MapPost("/charges/{chargeId:long}/waive", async (
            int terminalId, long chargeId, WaiveBody body, HttpContext http, ChargeService service, CancellationToken ct) =>
            TasApi.Admin(await service.WaiveAsync(TasApi.User(http)!, terminalId, chargeId, body.Reason, ct)));

        return app;
    }
}
