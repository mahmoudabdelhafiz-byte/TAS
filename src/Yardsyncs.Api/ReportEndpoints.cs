using Yardsyncs.Application.Auth;
using Yardsyncs.Application.Languages;
using Yardsyncs.Application.Reports;
using Yardsyncs.Infrastructure.Reports;

namespace Yardsyncs.Api;

/// <summary>
/// Reports as Excel files under /api/terminals/{terminalId}/exports, headed in the reader's language: the appointments
/// of a period (a company gets its own), the terminal's KPIs with every company (terminal staff), one company's report.
/// </summary>
public static class ReportEndpoints
{
    public static WebApplication MapReportExports(this WebApplication app)
    {
        var exports = app.MapGroup("/api/terminals/{terminalId:int}/exports").RequireAuthorization();

        exports.MapGet("/appointments", async (int terminalId, int? companyId, DateOnly from, DateOnly to, HttpContext http,
            ReportExportService service, CancellationToken ct) =>
        {
            var (company, denied) = Company(http, terminalId, companyId, required: false);
            if (denied is not null)
                return denied;
            if (Range(from, to) is { } bad)
                return bad;
            return await Run(async () => File(await service.AppointmentsAsync(terminalId, company, from, to, ServerText.Language, ct), "appointments", from, to));
        });

        exports.MapGet("/kpis", async (int terminalId, DateOnly from, DateOnly to, HttpContext http, ReportExportService service, CancellationToken ct) =>
        {
            if (!AccessRules.CanViewCapacity(YardsyncsApi.User(http)!, terminalId))
                return YardsyncsApi.Forbidden("Only terminal staff of this terminal see its KPIs.");
            if (Range(from, to) is { } bad)
                return bad;
            return await Run(async () => File(await service.KpisAsync(terminalId, from, to, ServerText.Language, ct), "kpis", from, to));
        });

        exports.MapGet("/company-report", async (int terminalId, int? companyId, DateOnly from, DateOnly to, HttpContext http,
            ReportExportService service, CancellationToken ct) =>
        {
            var (company, denied) = Company(http, terminalId, companyId, required: true);
            if (denied is not null)
                return denied;
            if (Range(from, to) is { } bad)
                return bad;
            return await Run(async () => await service.CompanyReportAsync(terminalId, company!.Value, from, to, ServerText.Language, ct) is { } bytes
                ? File(bytes, "company-report", from, to)
                : YardsyncsApi.Problem(StatusCodes.Status404NotFound, $"Company {company} not found."));
        });

        return app;
    }

    /// <summary>
    /// The company a report is for: a company user always gets their own; terminal staff any (or all, when not required).
    /// </summary>
    internal static (int? CompanyId, IResult? Denied) Company(HttpContext http, int terminalId, int? companyId, bool required)
    {
        var user = YardsyncsApi.User(http)!;
        if (!AccessRules.CanAccessTerminal(user, terminalId))
            return (null, YardsyncsApi.Forbidden("No access to this terminal."));
        if (user.IsCompanyUser)
        {
            var (own, error) = AccessRules.ResolveCompany(user, companyId, required: false);
            return error is null ? (own, null) : (null, YardsyncsApi.Forbidden(error));
        }
        if (!AccessRules.CanViewCapacity(user, terminalId))
            return (null, YardsyncsApi.Forbidden("Only terminal staff of this terminal and the company itself see its report."));
        if (required && companyId is null)
            return (null, YardsyncsApi.Problem(StatusCodes.Status400BadRequest, "Choose the company (companyId)."));
        return (companyId, null);
    }

    internal static IResult? Range(DateOnly from, DateOnly to) =>
        to < from
            ? YardsyncsApi.Problem(StatusCodes.Status400BadRequest, "'to' must be on or after 'from'.")
            : to.DayNumber - from.DayNumber + 1 > KpiCalculator.MaxDays
                ? YardsyncsApi.Problem(StatusCodes.Status400BadRequest, $"The range can be at most {KpiCalculator.MaxDays} days.")
                : null;

    private static IResult File(byte[] bytes, string name, DateOnly from, DateOnly to) =>
        Results.File(bytes, ReportExportService.ContentType, $"{name}-{from:yyyy-MM-dd}-{to:yyyy-MM-dd}.xlsx");

    private static async Task<IResult> Run(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (KeyNotFoundException ex)
        {
            return YardsyncsApi.Problem(StatusCodes.Status404NotFound, ex.Message);
        }
    }
}
