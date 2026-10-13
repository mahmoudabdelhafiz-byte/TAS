using Tas.Application.Auth;
using Tas.Application.Gate;
using Tas.Infrastructure.Gate;

namespace Tas.Api;

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
            Denied(http, terminalId) ?? TasApi.Admin(await service.ArriveAsync(TasApi.User(http)!, terminalId, body, ct)));

        gate.MapPost("/in", async (int terminalId, GateInInput body, HttpContext http, GateService service, CancellationToken ct) =>
            Denied(http, terminalId) ?? TasApi.Admin(await service.GateInAsync(TasApi.User(http)!, terminalId, body, ct)));

        gate.MapPost("/out", async (int terminalId, GateOutInput body, HttpContext http, GateService service, CancellationToken ct) =>
            Denied(http, terminalId) ?? TasApi.Admin(await service.GateOutAsync(TasApi.User(http)!, terminalId, body, ct)));

        gate.MapGet("/troubles", async (int terminalId, bool? openOnly, HttpContext http, GateService service, CancellationToken ct) =>
            Denied(http, terminalId) ?? Results.Ok(await service.ListTroublesAsync(terminalId, openOnly ?? true, ct)));

        gate.MapPost("/troubles/{troubleId:long}/resolve", async (
            int terminalId, long troubleId, ResolveTroubleInput body, HttpContext http, GateService service, CancellationToken ct) =>
            Denied(http, terminalId) ?? TasApi.Admin(await service.ResolveAsync(TasApi.User(http)!, terminalId, troubleId, body, ct)));

        gate.MapGet("/board", async (int terminalId, HttpContext http, GateService service, CancellationToken ct) =>
            Denied(http, terminalId) ?? Results.Ok(await service.BoardAsync(terminalId, ct)));

        gate.MapGet("/lanes", async (int terminalId, HttpContext http, GateService service, CancellationToken ct) =>
            Denied(http, terminalId) ?? Results.Ok(await service.ListLanesAsync(terminalId, ct)));

        gate.MapPost("/lanes", async (int terminalId, LaneInput body, HttpContext http, GateService service, CancellationToken ct) =>
            !AccessRules.CanManageCategories(TasApi.User(http)!, terminalId)
                ? TasApi.Forbidden("Only terminal admins of this terminal can add lanes.")
                : TasApi.Admin(await service.CreateLaneAsync(TasApi.User(http)!, terminalId, body, ct), l => $"/api/terminals/{terminalId}/gate/lanes/{l.LaneId}"));

        return app;
    }

    private static IResult? Denied(HttpContext http, int terminalId) =>
        AccessRules.CanOperateGate(TasApi.User(http)!, terminalId)
            ? null
            : TasApi.Forbidden("Only terminal staff of this terminal can work the gate.");
}
