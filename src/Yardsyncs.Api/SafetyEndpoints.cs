using Yardsyncs.Application.Auth;
using Yardsyncs.Application.Languages;
using Yardsyncs.Application.Safety;
using Yardsyncs.Infrastructure.Auth;
using Yardsyncs.Infrastructure.DriverApp;

namespace Yardsyncs.Api;

/// <summary>Safety in the driver app: campaigns and the induction (planners and terminal admins set them; staff see them).</summary>
public static class SafetyEndpoints
{
    public static WebApplication MapSafety(this WebApplication app)
    {
        var safety = app.MapGroup("/api/terminals/{terminalId:int}/safety").RequireAuthorization();

        safety.MapGet("/campaigns", async (int terminalId, HttpContext http, SafetyService service, CancellationToken ct) =>
            Staff(http, terminalId) ?? Results.Ok(await service.CampaignsAsync(terminalId, ct)));

        safety.MapPost("/campaigns", async (int terminalId, CampaignInput body, HttpContext http, SafetyService service, CancellationToken ct) =>
            Manage(http, terminalId) ?? YardsyncsApi.Admin(await service.SaveCampaignAsync(YardsyncsApi.User(http)!, terminalId, null, body, ct)));

        safety.MapPut("/campaigns/{campaignId:int}", async (int terminalId, int campaignId, CampaignInput body, HttpContext http, SafetyService service, CancellationToken ct) =>
            Manage(http, terminalId) ?? YardsyncsApi.Admin(await service.SaveCampaignAsync(YardsyncsApi.User(http)!, terminalId, campaignId, body, ct)));

        safety.MapPost("/campaigns/{campaignId:int}/archive", async (int terminalId, int campaignId, HttpContext http, SafetyService service, CancellationToken ct) =>
            Manage(http, terminalId) ?? YardsyncsApi.Admin(await service.ArchiveAsync(terminalId, campaignId, ct)));

        // The induction: 204 while the terminal has none.
        safety.MapGet("/induction", async (int terminalId, HttpContext http, SafetyService service, CancellationToken ct) =>
            Staff(http, terminalId) ?? (await service.InductionAsync(terminalId, ct) is { } induction ? Results.Ok(induction) : Results.NoContent()));

        safety.MapPut("/induction", async (int terminalId, InductionInput body, HttpContext http, SafetyService service, CancellationToken ct) =>
            Manage(http, terminalId) ?? YardsyncsApi.Admin(await service.SaveInductionAsync(YardsyncsApi.User(http)!, terminalId, body, ct)));

        // The driver app: notices to read and confirm, inductions to take, in the app's language.
        var driver = app.MapGroup("/api/driver/safety").RequireAuthorization(DriverAuth.Policy);
        driver.MapGet("/", async (HttpContext http, SafetyService service, CancellationToken ct) =>
        {
            var me = CurrentDriver.FromClaims(http.User)!;
            return Results.Ok(new
            {
                campaigns = await service.CampaignsForDriverAsync(me, ServerText.Language, ct),
                inductions = await service.InductionsForDriverAsync(me, ServerText.Language, ct),
            });
        });
        driver.MapPost("/campaigns/{campaignId:int}/confirm", async (int campaignId, HttpContext http, SafetyService service, CancellationToken ct) =>
            YardsyncsApi.Admin(await service.ConfirmAsync(CurrentDriver.FromClaims(http.User)!, campaignId, ServerText.Language, ct)));
        driver.MapPost("/inductions/{terminalId:int}/answers", async (int terminalId, AnswersInput body, HttpContext http, SafetyService service, CancellationToken ct) =>
            YardsyncsApi.Admin(await service.AnswerAsync(CurrentDriver.FromClaims(http.User)!, terminalId, body, ct)));

        return app;
    }

    private static IResult? Staff(HttpContext http, int terminalId) =>
        AccessRules.CanOperateGate(YardsyncsApi.User(http)!, terminalId) ? null : YardsyncsApi.Forbidden("Only terminal staff of this terminal see its safety campaigns and induction.");

    private static IResult? Manage(HttpContext http, int terminalId) =>
        AccessRules.CanManageCapacity(YardsyncsApi.User(http)!, terminalId) ? null : YardsyncsApi.Forbidden("Only planners and terminal admins of this terminal set safety campaigns and the induction.");
}
