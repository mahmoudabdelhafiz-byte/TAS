using Yardsyncs.Application.Auth;
using Yardsyncs.Application.Capacity;
using Yardsyncs.Infrastructure.Booking;

namespace Yardsyncs.Api;

public sealed record WaiveBody(string? Reason);

/// <summary>Fees: the terminal's fee schedule, the charges report (JSON or CSV) and waiving a charge.</summary>
public static class ChargeEndpoints
{
    public static WebApplication MapCharges(this WebApplication app)
    {
        var terminal = app.MapGroup("/api/terminals/{terminalId:int}").RequireAuthorization();

        // Anyone who books at the terminal sees its fees before booking.
        terminal.MapGet("/fees", async (int terminalId, HttpContext http, ChargeService service, CancellationToken ct) =>
            AccessRules.CanAccessTerminal(YardsyncsApi.User(http)!, terminalId)
                ? Results.Ok(await service.FeesAsync(terminalId, ct))
                : YardsyncsApi.Forbidden("You cannot see this terminal."));

        terminal.MapGet("/charges", async (
            int terminalId, DateOnly from, DateOnly to, int? companyId, string? format, HttpContext http, ChargeService service, CancellationToken ct) =>
        {
            var result = await service.ReportAsync(YardsyncsApi.User(http)!, terminalId, from, to, companyId, ct);
            if (result.Status != AdminStatus.Ok)
                return YardsyncsApi.Admin(result);
            if (string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase))
                return Results.File(System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(ChargeService.Csv(result.Value!))).ToArray(),
                    "text/csv; charset=utf-8", $"charges-{from:yyyy-MM-dd}-{to:yyyy-MM-dd}.csv");
            return Results.Ok(result.Value);
        });

        terminal.MapPost("/charges/{chargeId:long}/waive", async (
            int terminalId, long chargeId, WaiveBody body, HttpContext http, ChargeService service, CancellationToken ct) =>
            YardsyncsApi.Admin(await service.WaiveAsync(YardsyncsApi.User(http)!, terminalId, chargeId, body.Reason, ct)));

        return app;
    }
}
