using Yardsyncs.Application.Auth;
using Yardsyncs.Application.Capacity;
using Yardsyncs.Infrastructure.Capacity;

namespace Yardsyncs.Api;

public sealed record ReleaseRulesBody(IReadOnlyList<ReleaseRuleInput> Rules, string? Reason);

public sealed record SettingsBody(Dictionary<string, int> Values, string? Reason);

/// <summary>
/// Planner / terminal admin configuration under /api/terminals/{terminalId}.
/// Reading: terminal staff of the terminal. Changing: planners and terminal admins of the terminal.
/// </summary>
public static class CapacityEndpoints
{
    public static RouteGroupBuilder MapCapacityAdmin(this RouteGroupBuilder terminal)
    {
        var capacity = terminal.MapGroup("/capacity");

        // ---------------------------------------------------------- templates

        capacity.MapGet("/templates", async (int terminalId, bool? includeInactive, HttpContext http, CapacityAdminService service, CancellationToken ct) =>
            CanView(http, terminalId) ?? Results.Ok(await service.ListTemplatesAsync(terminalId, includeInactive ?? false, ct)));

        capacity.MapPost("/templates", async (int terminalId, TemplateInput body, HttpContext http, CapacityAdminService service, CancellationToken ct) =>
            CanManage(http, terminalId)
            ?? ToResult(await service.CreateTemplateAsync(YardsyncsApi.User(http)!, terminalId, body, ct), id => $"{Base(terminalId)}/templates/{id.TemplateId}"));

        capacity.MapPut("/templates/{templateId:int}", async (
            int terminalId, int templateId, TemplateInput body, HttpContext http, CapacityAdminService service, CancellationToken ct) =>
            CanManage(http, terminalId)
            ?? ToResult(await service.ReplaceTemplateAsync(YardsyncsApi.User(http)!, terminalId, templateId, body, ct)));

        capacity.MapPost("/templates/{templateId:int}/deactivate", async (
            int terminalId, int templateId, ReasonInput? body, HttpContext http, CapacityAdminService service, CancellationToken ct) =>
            CanManage(http, terminalId)
            ?? ToResult(await service.DeactivateTemplateAsync(YardsyncsApi.User(http)!, terminalId, templateId, body?.Reason, ct)));

        // ---------------------------------------------------------- overrides

        capacity.MapGet("/overrides", async (
            int terminalId, DateTimeOffset? from, DateTimeOffset? to, bool? includeInactive, HttpContext http, CapacityAdminService service, CancellationToken ct) =>
            CanView(http, terminalId) ?? Results.Ok(await service.ListOverridesAsync(terminalId, from, to, includeInactive ?? false, ct)));

        capacity.MapPost("/overrides", async (int terminalId, OverrideInput body, HttpContext http, CapacityAdminService service, CancellationToken ct) =>
            CanManage(http, terminalId)
            ?? ToResult(await service.CreateOverrideAsync(YardsyncsApi.User(http)!, terminalId, body, ct), o => $"{Base(terminalId)}/overrides/{o.OverrideId}"));

        capacity.MapPost("/overrides/{overrideId:int}/deactivate", async (
            int terminalId, int overrideId, ReasonInput? body, HttpContext http, CapacityAdminService service, CancellationToken ct) =>
            CanManage(http, terminalId)
            ?? ToResult(await service.DeactivateOverrideAsync(YardsyncsApi.User(http)!, terminalId, overrideId, body?.Reason, ct)));

        // ---------------------------------------------------------- reservations

        capacity.MapGet("/reservations", async (int terminalId, bool? includeInactive, HttpContext http, CapacityAdminService service, CancellationToken ct) =>
            CanView(http, terminalId) ?? Results.Ok(await service.ListReservationsAsync(terminalId, includeInactive ?? false, ct)));

        capacity.MapPost("/reservations", async (int terminalId, ReservationInput body, HttpContext http, CapacityAdminService service, CancellationToken ct) =>
            CanManage(http, terminalId)
            ?? ToResult(await service.CreateReservationAsync(YardsyncsApi.User(http)!, terminalId, body, ct), r => $"{Base(terminalId)}/reservations/{r.ReservationId}"));

        capacity.MapPost("/reservations/{reservationId:int}/deactivate", async (
            int terminalId, int reservationId, ReasonInput? body, HttpContext http, CapacityAdminService service, CancellationToken ct) =>
            CanManage(http, terminalId)
            ?? ToResult(await service.DeactivateReservationAsync(YardsyncsApi.User(http)!, terminalId, reservationId, body?.Reason, ct)));

        // ---------------------------------------------------------- quota release rules

        capacity.MapGet("/release-rules", async (int terminalId, HttpContext http, CapacityAdminService service, CancellationToken ct) =>
            CanView(http, terminalId) ?? Results.Ok(await service.GetReleaseRulesAsync(terminalId, ct)));

        capacity.MapPut("/release-rules", async (int terminalId, ReleaseRulesBody body, HttpContext http, CapacityAdminService service, CancellationToken ct) =>
            CanManage(http, terminalId)
            ?? ToResult(await service.ReplaceReleaseRulesAsync(YardsyncsApi.User(http)!, terminalId, body.Rules ?? new List<ReleaseRuleInput>(), body.Reason, ct)));

        // ---------------------------------------------------------- slot board

        capacity.MapGet("/slot-board", async (int terminalId, DateOnly date, HttpContext http, CapacityAdminService service, CancellationToken ct) =>
        {
            if (CanView(http, terminalId) is { } denied)
                return denied;
            try
            {
                return Results.Ok(await service.SlotBoardAsync(terminalId, date, ct));
            }
            catch (KeyNotFoundException ex)
            {
                return YardsyncsApi.Problem(StatusCodes.Status404NotFound, ex.Message);
            }
        });

        // ---------------------------------------------------------- settings & audit

        terminal.MapGet("/settings", async (int terminalId, HttpContext http, CapacityAdminService service, CancellationToken ct) =>
            CanView(http, terminalId) ?? Results.Ok(await service.GetSettingsAsync(terminalId, ct)));

        terminal.MapPut("/settings", async (int terminalId, SettingsBody body, HttpContext http, CapacityAdminService service, CancellationToken ct) =>
            CanManage(http, terminalId)
            ?? ToResult(await service.UpdateSettingsAsync(YardsyncsApi.User(http)!, terminalId, body.Values ?? new Dictionary<string, int>(), body.Reason, ct)));

        // Terminal KPIs for local dates from..to (at most 92 days): slot use, no-shows, punctuality, walk-ins, turn time.
        terminal.MapGet("/kpis", async (int terminalId, DateOnly from, DateOnly to, HttpContext http, Yardsyncs.Infrastructure.Reports.KpiService service, CancellationToken ct) =>
        {
            if (CanView(http, terminalId) is { } denied)
                return denied;
            if (to < from)
                return YardsyncsApi.Problem(StatusCodes.Status400BadRequest, "'to' must be on or after 'from'.");
            if (to.DayNumber - from.DayNumber + 1 > Yardsyncs.Application.Reports.KpiCalculator.MaxDays)
                return YardsyncsApi.Problem(StatusCodes.Status400BadRequest, $"The range can be at most {Yardsyncs.Application.Reports.KpiCalculator.MaxDays} days.");
            try
            {
                return Results.Ok(await service.ReportAsync(terminalId, from, to, ct));
            }
            catch (KeyNotFoundException ex)
            {
                return YardsyncsApi.Problem(StatusCodes.Status404NotFound, ex.Message);
            }
        });

        terminal.MapGet("/audit", async (int terminalId, string? entity, int? limit, HttpContext http, CapacityAdminService service, CancellationToken ct) =>
            CanManage(http, terminalId) ?? Results.Ok(await service.AuditAsync(terminalId, entity, limit ?? 100, ct)));

        return terminal;
    }

    private static string Base(int terminalId) => $"/api/terminals/{terminalId}/capacity";

    /// <summary>Null when allowed; otherwise the 403 response.</summary>
    private static IResult? CanView(HttpContext http, int terminalId) =>
        AccessRules.CanViewCapacity(YardsyncsApi.User(http)!, terminalId)
            ? null
            : YardsyncsApi.Forbidden("Only terminal staff of this terminal can see its capacity configuration.");

    private static IResult? CanManage(HttpContext http, int terminalId) =>
        AccessRules.CanManageCapacity(YardsyncsApi.User(http)!, terminalId)
            ? null
            : YardsyncsApi.Forbidden("Only planners and terminal admins of this terminal can change capacity.");

    private static IResult ToResult<T>(AdminResult<T> result, Func<T, string>? location = null) => YardsyncsApi.Admin(result, location);
}
