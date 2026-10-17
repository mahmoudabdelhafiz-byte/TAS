using Yardsyncs.Application.Auth;
using Yardsyncs.Application.MasterData;
using Yardsyncs.Infrastructure.Auth;
using Yardsyncs.Infrastructure.DriverApp;

namespace Yardsyncs.Api;

public sealed record WithdrawBody(string? Reason);

/// <summary>
/// The driver record: terminal staff record entries; planners and terminal admins withdraw them and decide disputes;
/// companies see their own drivers' records when the terminal allows it; the driver sees and disputes in the app.
/// </summary>
public static class DriverRecordEndpoints
{
    public static WebApplication MapDriverRecords(this WebApplication app)
    {
        var terminal = app.MapGroup("/api/terminals/{terminalId:int}").RequireAuthorization();

        terminal.MapGet("/driver-records", async (int terminalId, string? q, HttpContext http, DriverRecordService service, CancellationToken ct) =>
        {
            var (companyId, denied) = await ViewerAsync(http, terminalId, service, ct);
            return denied ?? Results.Ok(await service.ListAsync(terminalId, companyId, q, ct));
        });

        terminal.MapGet("/driver-records/{driverId:int}", async (int terminalId, int driverId, HttpContext http, DriverRecordService service, CancellationToken ct) =>
        {
            var (companyId, denied) = await ViewerAsync(http, terminalId, service, ct);
            if (denied is not null)
                return denied;
            return await service.RecordAsync(terminalId, driverId, companyId, ct) is { } record
                ? Results.Ok(record)
                : YardsyncsApi.Problem(StatusCodes.Status404NotFound, $"Driver {driverId} not found.");
        });

        // Any staff of the terminal records (gate, control room, yard supervisors).
        terminal.MapPost("/driver-records/{driverId:int}/incidents", async (int terminalId, int driverId, IncidentInput body, HttpContext http,
            DriverRecordService service, CancellationToken ct) =>
            !AccessRules.CanOperateGate(YardsyncsApi.User(http)!, terminalId)
                ? YardsyncsApi.Forbidden("Only terminal staff of this terminal record entries on a driver's record.")
                : YardsyncsApi.Admin(await service.AddAsync(YardsyncsApi.User(http)!, terminalId, driverId, body, ct)));

        terminal.MapPost("/driver-incidents/{incidentId:long}/withdraw", async (int terminalId, long incidentId, WithdrawBody body, HttpContext http,
            DriverRecordService service, CancellationToken ct) =>
            Decides(http, terminalId) ?? YardsyncsApi.Admin(await service.WithdrawAsync(YardsyncsApi.User(http)!, terminalId, incidentId, body.Reason, ct)));

        terminal.MapPost("/driver-incidents/{incidentId:long}/decision", async (int terminalId, long incidentId, DisputeDecisionInput body, HttpContext http,
            DriverRecordService service, CancellationToken ct) =>
            Decides(http, terminalId) ?? YardsyncsApi.Admin(await service.DecideAsync(YardsyncsApi.User(http)!, terminalId, incidentId, body, ct)));

        // The driver's own record, across terminals, and disputing an entry.
        var driver = app.MapGroup("/api/driver").RequireAuthorization(DriverAuth.Policy);
        driver.MapGet("/record", async (HttpContext http, DriverRecordService service, CancellationToken ct) =>
            Results.Ok(await service.ForDriverAsync(CurrentDriver.FromClaims(http.User)!, ct)));
        driver.MapPost("/incidents/{incidentId:long}/dispute", async (long incidentId, DisputeInput body, HttpContext http, DriverRecordService service, CancellationToken ct) =>
            YardsyncsApi.Admin(await service.DisputeAsync(CurrentDriver.FromClaims(http.User)!, incidentId, body, ct)));

        return app;
    }

    /// <summary>Terminal staff see every driver; a company its own drivers, when the terminal allows it.</summary>
    private static async Task<(int? CompanyId, IResult? Denied)> ViewerAsync(HttpContext http, int terminalId, DriverRecordService service, CancellationToken ct)
    {
        var user = YardsyncsApi.User(http)!;
        if (AccessRules.CanOperateGate(user, terminalId))
            return (null, null);
        if (user.IsCompanyUser && user.CompanyId is { } own && AccessRules.CanAccessTerminal(user, terminalId))
            return await service.CompanySeesAsync(terminalId, ct)
                ? (own, null)
                : (null, YardsyncsApi.Forbidden("This terminal doesn't share drivers' records with companies."));
        return (null, YardsyncsApi.Forbidden("No access to this terminal."));
    }

    private static IResult? Decides(HttpContext http, int terminalId) =>
        AccessRules.CanManageCapacity(YardsyncsApi.User(http)!, terminalId)
            ? null
            : YardsyncsApi.Forbidden("Only planners and terminal admins of this terminal withdraw entries and decide disputes.");
}
