using Yardsyncs.Application.Auth;
using Yardsyncs.Application.Gate;

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

    private static IResult? NotAdmin(HttpContext http, int terminalId) =>
        AccessRules.CanManageCategories(YardsyncsApi.User(http)!, terminalId)
            ? null
            : YardsyncsApi.Forbidden("Only terminal admins of this terminal can set up lanes and their devices.");
}
