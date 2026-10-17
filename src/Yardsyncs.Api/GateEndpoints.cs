using Yardsyncs.Application.Capacity;
using Yardsyncs.Application.Auth;
using Yardsyncs.Application.Gate;
using Yardsyncs.Infrastructure.Gate;

namespace Yardsyncs.Api;

/// <summary>
/// The truck appointment system's gate under /api/terminals/{terminalId}/gate — for gate clerks, supervisors and the
/// terminal's own gate system (Yardsyncs Autogate or a third-party one, as a gate system account). All terminal staff
/// of the terminal; lanes are created by terminal admins.
/// </summary>
public static class GateEndpoints
{
    public static WebApplication MapGate(this WebApplication app)
    {
        var gate = app.MapGroup("/api/terminals/{terminalId:int}/gate").RequireAuthorization();

        // A truck at an entry lane: match, re-check, admit or refer to the trouble desk.
        gate.MapPost("/arrivals", async (int terminalId, ArrivalInput body, HttpContext http, GateService service, CancellationToken ct) =>
            Denied(http, terminalId) ?? YardsyncsApi.Admin(ForReader(await service.ArriveAsync(YardsyncsApi.User(http)!, terminalId, body, ct))));

        // The same, asked only: what the gate would decide, recording nothing (for the terminal's own gate system).
        gate.MapPost("/arrivals/check", async (int terminalId, ArrivalInput body, HttpContext http, GateService service, CancellationToken ct) =>
            Denied(http, terminalId) ?? YardsyncsApi.Admin(ForReader(await service.ArriveAsync(YardsyncsApi.User(http)!, terminalId, body, ct, record: false))));

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

        gate.MapPost("/troubles/{troubleId:long}/resolve", async (
            int terminalId, long troubleId, ResolveTroubleInput body, HttpContext http, GateService service, CancellationToken ct) =>
            Denied(http, terminalId) ?? YardsyncsApi.Admin(await service.ResolveAsync(YardsyncsApi.User(http)!, terminalId, troubleId, body, ct)));

        gate.MapGet("/board", async (int terminalId, HttpContext http, GateService service, CancellationToken ct) =>
            Denied(http, terminalId) ?? Results.Ok(await service.BoardAsync(terminalId, ct)));

        // Lanes: gate staff list them and open / close them; terminal admins add and retire them (devices and automatic
        // working come with the Autogate module).
        gate.MapGet("/lanes", async (int terminalId, HttpContext http, LaneService service, CancellationToken ct) =>
            Denied(http, terminalId) ?? Results.Ok(await service.ListAsync(terminalId, ct)));

        gate.MapPost("/lanes", async (int terminalId, LaneInput body, HttpContext http, LaneService service, CancellationToken ct) =>
            NotAdmin(http, terminalId) ?? YardsyncsApi.Admin(await service.CreateAsync(YardsyncsApi.User(http)!, terminalId, body, ct), l => $"/api/terminals/{terminalId}/gate/lanes/{l.LaneId}"));

        gate.MapPut("/lanes/{laneId:int}", async (int terminalId, int laneId, LaneUpdateInput body, HttpContext http, LaneService service, CancellationToken ct) =>
            NotAdmin(http, terminalId) ?? YardsyncsApi.Admin(await service.UpdateAsync(YardsyncsApi.User(http)!, terminalId, laneId, body, ct)));

        gate.MapPost("/lanes/{laneId:int}/state", async (int terminalId, int laneId, LaneStateInput body, HttpContext http, LaneService service, CancellationToken ct) =>
            Denied(http, terminalId) ?? YardsyncsApi.Admin(await service.SetStateAsync(YardsyncsApi.User(http)!, terminalId, laneId, body, ct)));

        // The terminal's own gate system: keys for it to use this API (X-Api-Key), made and revoked by terminal admins.
        gate.MapGet("/systems", async (int terminalId, HttpContext http, GateSystemService service, CancellationToken ct) =>
            NotAdmin(http, terminalId) ?? Results.Ok(await service.ListAsync(terminalId, ct)));

        gate.MapPost("/systems", async (int terminalId, GateSystemInput body, HttpContext http, GateSystemService service, CancellationToken ct) =>
            NotAdmin(http, terminalId) ?? YardsyncsApi.Admin(await service.CreateAsync(YardsyncsApi.User(http)!, terminalId, body, ct), k => $"/api/terminals/{terminalId}/gate/systems/{k.System.KeyId}"));

        gate.MapDelete("/systems/{keyId:int}", async (int terminalId, int keyId, HttpContext http, GateSystemService service, CancellationToken ct) =>
            NotAdmin(http, terminalId) ?? YardsyncsApi.Admin(await service.RevokeAsync(YardsyncsApi.User(http)!, terminalId, keyId, ct)));

        return app;
    }

    private static IResult? Denied(HttpContext http, int terminalId) =>
        AccessRules.CanOperateGate(YardsyncsApi.User(http)!, terminalId)
            ? null
            : YardsyncsApi.Forbidden("Only terminal staff of this terminal can work the gate.");

    private static IResult? NotAdmin(HttpContext http, int terminalId) =>
        AccessRules.CanManageCategories(YardsyncsApi.User(http)!, terminalId)
            ? null
            : YardsyncsApi.Forbidden("Only terminal admins of this terminal can set up lanes, their devices and gate systems.");

    private static AdminResult<ArrivalResult> ForReader(AdminResult<ArrivalResult> result) =>
        result.Value is null ? result : result with { Value = result.Value.ForReader() };
}
