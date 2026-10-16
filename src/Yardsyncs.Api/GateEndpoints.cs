using Yardsyncs.Application.Auth;
using Yardsyncs.Application.Gate;
using Yardsyncs.Infrastructure.Gate;

namespace Yardsyncs.Api;

/// <summary>
/// Gate operations under /api/terminals/{terminalId}/gate — for gate clerks, the Autogate / OCR system
/// (as a GateClerk account) and supervisors. All terminal staff of the terminal; lanes are created by terminal admins.
/// </summary>
public static class GateEndpoints
{
    public static WebApplication MapGate(this WebApplication app)
    {
        var gate = app.MapGroup("/api/terminals/{terminalId:int}/gate").RequireAuthorization();

        // A truck at an entry lane: match, re-check, admit or refer to the trouble desk.
        gate.MapPost("/arrivals", async (int terminalId, ArrivalInput body, HttpContext http, GateService service, CancellationToken ct) =>
            Denied(http, terminalId) ?? YardsyncsApi.Admin(await service.ArriveAsync(YardsyncsApi.User(http)!, terminalId, body, ct)));

        gate.MapPost("/in", async (int terminalId, GateInInput body, HttpContext http, GateService service, CancellationToken ct) =>
            Denied(http, terminalId) ?? YardsyncsApi.Admin(await service.GateInAsync(YardsyncsApi.User(http)!, terminalId, body, ct)));

        gate.MapPost("/out", async (int terminalId, GateOutInput body, HttpContext http, GateService service, CancellationToken ct) =>
            Denied(http, terminalId) ?? YardsyncsApi.Admin(await service.GateOutAsync(YardsyncsApi.User(http)!, terminalId, body, ct)));

        // Control room: every live visit with its stops in the yard and where the truck was last seen; today's times.
        app.MapGet("/api/terminals/{terminalId:int}/control-room", async (
            int terminalId, HttpContext http, Yardsyncs.Infrastructure.ControlRoom.ControlRoomService service, CancellationToken ct) =>
            Denied(http, terminalId) ?? Results.Ok(await service.BoardAsync(terminalId, ct)))
            .RequireAuthorization();

        gate.MapGet("/troubles", async (int terminalId, bool? openOnly, HttpContext http, GateService service, CancellationToken ct) =>
            Denied(http, terminalId) ?? Results.Ok(await service.ListTroublesAsync(terminalId, openOnly ?? true, ct)));

        // At an automatic lane, letting a referred truck in opens that lane's barrier.
        gate.MapPost("/troubles/{troubleId:long}/resolve", async (
            int terminalId, long troubleId, ResolveTroubleInput body, HttpContext http, GateService service, LaneControllerService controller, CancellationToken ct) =>
        {
            if (Denied(http, terminalId) is { } denied)
                return denied;
            var result = await service.ResolveAsync(YardsyncsApi.User(http)!, terminalId, troubleId, body, ct);
            if (result.Value is { Resolution: { } resolution })
                await controller.OnTroubleResolvedAsync(troubleId, resolution, ct);
            return YardsyncsApi.Admin(result);
        });

        gate.MapGet("/board", async (int terminalId, HttpContext http, GateService service, CancellationToken ct) =>
            Denied(http, terminalId) ?? Results.Ok(await service.BoardAsync(terminalId, ct)));

        // Lanes (Autogate lane model): gate staff list them and open / close them; terminal admins add lanes and devices
        // and switch a lane to automatic.
        gate.MapGet("/lanes", async (int terminalId, HttpContext http, LaneService service, CancellationToken ct) =>
            Denied(http, terminalId) ?? Results.Ok(await service.ListAsync(terminalId, ct)));

        gate.MapPost("/lanes", async (int terminalId, LaneInput body, HttpContext http, LaneService service, CancellationToken ct) =>
            NotAdmin(http, terminalId) ?? YardsyncsApi.Admin(await service.CreateAsync(YardsyncsApi.User(http)!, terminalId, body, ct), l => $"/api/terminals/{terminalId}/gate/lanes/{l.LaneId}"));

        gate.MapPut("/lanes/{laneId:int}", async (int terminalId, int laneId, LaneUpdateInput body, HttpContext http, LaneService service, CancellationToken ct) =>
            NotAdmin(http, terminalId) ?? YardsyncsApi.Admin(await service.UpdateAsync(YardsyncsApi.User(http)!, terminalId, laneId, body, ct)));

        gate.MapPost("/lanes/{laneId:int}/state", async (int terminalId, int laneId, LaneStateInput body, HttpContext http, LaneService service, CancellationToken ct) =>
            Denied(http, terminalId) ?? YardsyncsApi.Admin(await service.SetStateAsync(YardsyncsApi.User(http)!, terminalId, laneId, body, ct)));

        gate.MapPost("/lanes/{laneId:int}/devices", async (int terminalId, int laneId, LaneDeviceInput body, HttpContext http, LaneService service, CancellationToken ct) =>
            NotAdmin(http, terminalId) ?? YardsyncsApi.Admin(await service.AddDeviceAsync(YardsyncsApi.User(http)!, terminalId, laneId, body, ct), l => $"/api/terminals/{terminalId}/gate/lanes/{l.LaneId}"));

        gate.MapPut("/lanes/{laneId:int}/devices/{deviceId:int}", async (
            int terminalId, int laneId, int deviceId, LaneDeviceInput body, HttpContext http, LaneService service, CancellationToken ct) =>
            NotAdmin(http, terminalId) ?? YardsyncsApi.Admin(await service.UpdateDeviceAsync(YardsyncsApi.User(http)!, terminalId, laneId, deviceId, body, ct)));

        gate.MapDelete("/lanes/{laneId:int}/devices/{deviceId:int}", async (
            int terminalId, int laneId, int deviceId, HttpContext http, LaneService service, CancellationToken ct) =>
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
        gate.MapGet("/tos-records", async (int terminalId, bool? problemsOnly, HttpContext http, Yardsyncs.Infrastructure.Tos.TosGateService service, CancellationToken ct) =>
            Denied(http, terminalId) ?? Results.Ok(await service.ListAsync(terminalId, problemsOnly ?? false, ct)));

        gate.MapPost("/tos-records/send", async (int terminalId, HttpContext http, Yardsyncs.Infrastructure.Tos.TosGateService service, CancellationToken ct) =>
            NotAdmin(http, terminalId) ?? Results.Ok(new { tried = await service.SendDueAsync(terminalId, ct) }));

        gate.MapPost("/tos-records/{messageId:long}/retry", async (int terminalId, long messageId, HttpContext http, Yardsyncs.Infrastructure.Tos.TosGateService service, CancellationToken ct) =>
            NotAdmin(http, terminalId) ?? YardsyncsApi.Admin(await service.RetryAsync(terminalId, messageId, ct)));

        gate.MapPost("/lanes/{laneId:int}/devices/{deviceId:int}/key", async (
            int terminalId, int laneId, int deviceId, HttpContext http, LaneService service, CancellationToken ct) =>
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
