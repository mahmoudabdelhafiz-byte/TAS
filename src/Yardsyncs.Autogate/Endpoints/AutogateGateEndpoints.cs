using Yardsyncs.Application.Auth;
using Yardsyncs.Application.Gate;
using Yardsyncs.Infrastructure.Auth;

namespace Yardsyncs.Autogate.Endpoints;

/// <summary>
/// Autogate under /api/terminals/{terminalId}/gate: lane devices and their keys, commands, lane activity, the lane
/// controller's trucks (remote clerk: detail, check again, let out), the gate ticket and the gate records in the TOS.
/// Only for terminals with the Autogate module (404 otherwise). Gate staff of the terminal; devices and keys: terminal admins.
/// </summary>
public static class AutogateGateEndpoints
{
    public static WebApplication MapAutogateGate(this WebApplication app)
    {
        var gate = AutogateModule.TerminalGroup(app, "/api/terminals/{terminalId:int}/gate");

        gate.MapPost("/lanes/{laneId:int}/devices", async (int terminalId, int laneId, LaneDeviceInput body, HttpContext http, LaneDeviceService service, CancellationToken ct) =>
            NotAdmin(http, terminalId) ?? YardsyncsApi.Admin(await service.AddDeviceAsync(YardsyncsApi.User(http)!, terminalId, laneId, body, ct), l => $"/api/terminals/{terminalId}/gate/lanes/{l.LaneId}"));

        gate.MapPut("/lanes/{laneId:int}/devices/{deviceId:int}", async (
            int terminalId, int laneId, int deviceId, LaneDeviceInput body, HttpContext http, LaneDeviceService service, CancellationToken ct) =>
            NotAdmin(http, terminalId) ?? YardsyncsApi.Admin(await service.UpdateDeviceAsync(YardsyncsApi.User(http)!, terminalId, laneId, deviceId, body, ct)));

        gate.MapDelete("/lanes/{laneId:int}/devices/{deviceId:int}", async (
            int terminalId, int laneId, int deviceId, HttpContext http, LaneDeviceService service, CancellationToken ct) =>
            NotAdmin(http, terminalId) ?? YardsyncsApi.Admin(await service.RemoveDeviceAsync(YardsyncsApi.User(http)!, terminalId, laneId, deviceId, ct)));

        // Device adapters: gate staff send commands (open the barrier…) and see a lane's latest reads and commands;
        // terminal admins issue device keys.
        gate.MapPost("/lanes/{laneId:int}/commands", async (int terminalId, int laneId, LaneCommandInput body, HttpContext http, DeviceService service, CancellationToken ct) =>
            Denied(http, terminalId) ?? YardsyncsApi.Admin(await service.SendAsync(YardsyncsApi.User(http)!, terminalId, laneId, body, ct)));

        gate.MapGet("/lanes/{laneId:int}/activity", async (int terminalId, int laneId, int? limit, HttpContext http, DeviceService service, CancellationToken ct) =>
            Denied(http, terminalId) ?? (await service.ActivityAsync(terminalId, laneId, limit ?? 20, ct) is { } activity
                ? Results.Ok(activity)
                : YardsyncsApi.Problem(StatusCodes.Status404NotFound, $"Lane {laneId} not found.")));

        // Edge mode: the terminal's lane agent, signed in with a gate system key, keeps a snapshot to run the automatic
        // lanes when the cloud can't be reached, and sends back what it decided meanwhile.
        gate.MapGet("/edge/snapshot", async (int terminalId, HttpContext http, EdgeService service, CancellationToken ct) =>
            NotAgent(http, terminalId) ?? (await service.SnapshotAsync(terminalId, ct) is { } snapshot
                ? Results.Ok(snapshot)
                : YardsyncsApi.Problem(StatusCodes.Status404NotFound, $"Terminal {terminalId} not found.")));

        gate.MapPost("/edge/passages", async (int terminalId, List<Yardsyncs.Application.Edge.EdgePassage> body, HttpContext http, EdgeService service, CancellationToken ct) =>
        {
            if (NotAgent(http, terminalId) is { } denied)
                return denied;
            if (body.Count > EdgeService.MaxPassagesPerCall)
                return YardsyncsApi.Problem(StatusCodes.Status400BadRequest, $"At most {EdgeService.MaxPassagesPerCall} passages per call.");
            return Results.Ok(await service.RecordAsync(YardsyncsApi.User(http)!, terminalId, body, ct));
        });

        // Lane KPIs for local dates from..to (at most 92 days): terminal staff, like the terminal's other KPIs.
        gate.MapGet("/lane-kpis", async (int terminalId, DateOnly from, DateOnly to, HttpContext http, LaneKpiService service, CancellationToken ct) =>
        {
            if (!AccessRules.CanViewCapacity(YardsyncsApi.User(http)!, terminalId))
                return YardsyncsApi.Forbidden("Only terminal staff of this terminal can see its KPIs.");
            if (to < from)
                return YardsyncsApi.Problem(StatusCodes.Status400BadRequest, "'to' must be on or after 'from'.");
            if (to.DayNumber - from.DayNumber + 1 > Yardsyncs.Application.Reports.KpiCalculator.MaxDays)
                return YardsyncsApi.Problem(StatusCodes.Status400BadRequest, $"The range can be at most {Yardsyncs.Application.Reports.KpiCalculator.MaxDays} days.");
            return await service.ReportAsync(terminalId, from, to, ct) is { } report
                ? Results.Ok(report)
                : YardsyncsApi.Problem(StatusCodes.Status404NotFound, $"Terminal {terminalId} not found.");
        });

        // Lane controller: each truck's passage through an automatic lane.
        gate.MapGet("/transactions", async (int terminalId, int? laneId, int? limit, HttpContext http, LaneControllerService service, CancellationToken ct) =>
            Denied(http, terminalId) ?? Results.Ok(await service.ListAsync(terminalId, laneId, limit ?? 50, ct)));

        // Remote clerk station: one truck's reads, photos, commands and case; check again after correcting misreads.
        gate.MapGet("/transactions/{transactionId:long}", async (int terminalId, long transactionId, HttpContext http, LaneControllerService service, CancellationToken ct) =>
            Denied(http, terminalId) ?? (await service.DetailAsync(terminalId, transactionId, ct) is { } detail
                ? Results.Ok(detail)
                : YardsyncsApi.Problem(StatusCodes.Status404NotFound, $"Truck transaction {transactionId} not found.")));

        gate.MapPost("/transactions/{transactionId:long}/recheck", async (
            int terminalId, long transactionId, TransactionCorrectionInput body, HttpContext http, LaneControllerService service, CancellationToken ct) =>
            Denied(http, terminalId) ?? YardsyncsApi.Admin(await service.RecheckAsync(YardsyncsApi.User(http)!, terminalId, transactionId, body, ct)));

        // The gate ticket (equipment interchange receipt) of a visit.
        gate.MapGet("/tickets/{appointmentNo}", async (int terminalId, string appointmentNo, HttpContext http, GateTicketService service, CancellationToken ct) =>
            Denied(http, terminalId) ?? (await service.TicketAsync(terminalId, appointmentNo, ct) is { } ticket
                ? Results.Ok(ticket)
                : YardsyncsApi.Problem(StatusCodes.Status404NotFound, $"Appointment {appointmentNo} not found.")));

        // Gate passages recorded in the TOS (terminals with tos_gate_write): what is waiting or failed; admins send now / again.
        gate.MapGet("/tos-records", async (int terminalId, bool? problemsOnly, HttpContext http, TosGateService service, CancellationToken ct) =>
            Denied(http, terminalId) ?? Results.Ok(await service.ListAsync(terminalId, problemsOnly ?? false, ct)));

        gate.MapPost("/tos-records/send", async (int terminalId, HttpContext http, TosGateService service, CancellationToken ct) =>
            NotAdmin(http, terminalId) ?? Results.Ok(new { tried = await service.SendDueAsync(terminalId, ct) }));

        gate.MapPost("/tos-records/{messageId:long}/retry", async (int terminalId, long messageId, HttpContext http, TosGateService service, CancellationToken ct) =>
            NotAdmin(http, terminalId) ?? YardsyncsApi.Admin(await service.RetryAsync(terminalId, messageId, ct)));

        gate.MapPost("/transactions/{transactionId:long}/let-out", async (
            int terminalId, long transactionId, LetOutInput body, HttpContext http, LaneControllerService service, CancellationToken ct) =>
            Denied(http, terminalId) ?? YardsyncsApi.Admin(await service.LetOutAsync(YardsyncsApi.User(http)!, terminalId, transactionId, body.Note, ct)));

        gate.MapPost("/lanes/{laneId:int}/devices/{deviceId:int}/key", async (
            int terminalId, int laneId, int deviceId, HttpContext http, LaneDeviceService service, CancellationToken ct) =>
            NotAdmin(http, terminalId) ?? YardsyncsApi.Admin(await service.IssueKeyAsync(YardsyncsApi.User(http)!, terminalId, laneId, deviceId, ct)));

        return app;
    }

    private static IResult? Denied(HttpContext http, int terminalId) =>
        AccessRules.CanOperateGate(YardsyncsApi.User(http)!, terminalId)
            ? null
            : YardsyncsApi.Forbidden("Only terminal staff of this terminal can work the gate.");

    /// <summary>Only the terminal's own lane agent, with a gate system key: the snapshot holds device fingerprints and pass secrets.</summary>
    private static IResult? NotAgent(HttpContext http, int terminalId) =>
        http.User.Identity?.AuthenticationType == GateSystemAuth.Scheme && AccessRules.CanOperateGate(YardsyncsApi.User(http)!, terminalId)
            ? null
            : YardsyncsApi.Forbidden("Only the terminal's lane agent, with a gate system key of this terminal, can use edge mode.");

    private static IResult? NotAdmin(HttpContext http, int terminalId) =>
        AccessRules.CanManageCategories(YardsyncsApi.User(http)!, terminalId)
            ? null
            : YardsyncsApi.Forbidden("Only terminal admins of this terminal can set up lanes and their devices.");
}
