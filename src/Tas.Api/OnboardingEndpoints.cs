using Tas.Application.Capacity;
using Tas.Infrastructure.Onboarding;

namespace Tas.Api;

/// <summary>Adding a terminal and its set-up checklist.</summary>
public static class OnboardingEndpoints
{
    public static WebApplication MapOnboarding(this WebApplication app)
    {
        app.MapPost("/api/terminals", async (NewTerminalInput body, HttpContext http, OnboardingService service, CancellationToken ct) =>
            TasApi.Admin(await service.CreateAsync(TasApi.User(http)!, body, ct), t => $"/api/terminals/{t.TerminalId}")).RequireAuthorization();

        var setup = app.MapGroup("/api/terminals/{terminalId:int}/setup").RequireAuthorization();

        setup.MapGet("/", async (int terminalId, HttpContext http, OnboardingService service, CancellationToken ct) =>
        {
            var result = await service.StatusAsync(TasApi.User(http)!, terminalId, ct);
            return result.Status == AdminStatus.Ok ? Results.Ok(result.Value) : TasApi.Admin(result);
        });

        setup.MapPost("/standard-move-types", async (int terminalId, HttpContext http, OnboardingService service, CancellationToken ct) =>
            TasApi.Admin(await service.AddStandardMoveTypesAsync(TasApi.User(http)!, terminalId, ct)));

        setup.MapPost("/tos-test", async (int terminalId, HttpContext http, OnboardingService service, CancellationToken ct) =>
            TasApi.Admin(await service.TestTosAsync(TasApi.User(http)!, terminalId, ct)));

        return app;
    }
}
