using Tas.Application.Capacity;
using Tas.Infrastructure.Brands;

namespace Tas.Api;

/// <summary>Branding: what the sign-in page, the portal and the app show, and its settings.</summary>
public static class BrandingEndpoints
{
    public static WebApplication MapBranding(this WebApplication app)
    {
        // Public: the sign-in page and the drivers' app show it before anyone signs in. An operator's own web address
        // gets the operator's brand.
        app.MapGet("/api/branding", async (HttpContext http, BrandingService service, CancellationToken ct) =>
            Results.Ok(await service.ForHostAsync(http.Request.Host.Host, ct))).AllowAnonymous();

        app.MapGet("/api/terminals/{terminalId:int}/branding", async (int terminalId, HttpContext http, BrandingService service, CancellationToken ct) =>
            Tas.Application.Auth.AccessRules.CanAccessTerminal(TasApi.User(http)!, terminalId)
                ? Results.Ok(await service.ForTerminalAsync(terminalId, ct))
                : TasApi.Forbidden("You cannot see this terminal.")).RequireAuthorization();

        // The logo: its address changes with every new logo, so it can be kept a long time.
        app.MapGet("/api/branding/{brandingId:int}/logo", async (int brandingId, HttpContext http, BrandingService service, CancellationToken ct) =>
        {
            if (await service.LogoAsync(brandingId, ct) is not { } logo)
                return TasApi.Problem(StatusCodes.Status404NotFound, "No logo.");
            http.Response.Headers.CacheControl = "public, max-age=604800";
            http.Response.Headers.XContentTypeOptions = "nosniff";
            http.Response.Headers.ContentSecurityPolicy = "default-src 'none'";
            return Results.Bytes(logo.Logo, logo.Type, lastModified: logo.UpdatedAt);
        }).AllowAnonymous();

        // Settings: "installation" (system admins) or an operator (system admins and that operator's admins).
        foreach (var (route, operatorOf) in new (string, Func<HttpContext, int?>)[]
        {
            ("/api/branding/installation", _ => null),
            ("/api/operators/{operatorId:int}/branding", http => int.Parse(http.Request.RouteValues["operatorId"]!.ToString()!, System.Globalization.CultureInfo.InvariantCulture)),
        })
        {
            var group = app.MapGroup(route).RequireAuthorization();

            group.MapGet("/", async (HttpContext http, BrandingService service, CancellationToken ct) =>
                Result(await service.GetAsync(TasApi.User(http)!, operatorOf(http), ct)));

            group.MapPut("/", async (BrandInput body, HttpContext http, BrandingService service, CancellationToken ct) =>
                TasApi.Admin(await service.UpdateAsync(TasApi.User(http)!, operatorOf(http), body, ct)));

            group.MapPost("/logo", async (IFormFile? file, HttpContext http, BrandingService service, CancellationToken ct) =>
            {
                if (file is null || file.Length is 0 or > BrandingService.MaxLogoBytes)
                    return TasApi.Problem(StatusCodes.Status400BadRequest, $"Send the logo as the form field 'file' (PNG, JPEG or WebP, at most {BrandingService.MaxLogoBytes / 1024} KB).");
                using var stream = new MemoryStream();
                await file.CopyToAsync(stream, ct);
                return TasApi.Admin(await service.SetLogoAsync(TasApi.User(http)!, operatorOf(http), stream.ToArray(), ct));
            }).DisableAntiforgery().WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(BrandingService.MaxLogoBytes + 16 * 1024));

            group.MapDelete("/logo", async (HttpContext http, BrandingService service, CancellationToken ct) =>
                TasApi.Admin(await service.SetLogoAsync(TasApi.User(http)!, operatorOf(http), null, ct)));
        }

        return app;
    }

    private static IResult Result(AdminResult<BrandSettings> result) =>
        result.Status == AdminStatus.Ok ? Results.Ok(result.Value) : TasApi.Admin(result);
}
