using Tas.Application.Auth;
using Tas.Application.Capacity;
using Tas.Application.MasterData;
using Tas.Domain.Booking;
using Tas.Infrastructure.Auth;
using Tas.Infrastructure.MasterData;
using Tas.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Tas.Api;

public sealed record ActiveBody(bool Active, string? Reason);

public sealed record TerminalView(
    int TerminalId, string Code, string Name, Tas.Domain.TerminalType TerminalType, string TimeZone, int SlotMinutes, int BookingHorizonDays,
    int? OperatorId = null, string Country = "SA", string Currency = "SAR");

public sealed record TerminalOperatorBody(int? OperatorId);

/// <summary>A country pack as the portal and the drivers' app need it.</summary>
public sealed record CountryView(
    string Code, string NameEn, string NameAr, string DialCode, string MobileExample, string IdNameEn, string IdNameAr,
    string Currency, int CurrencyDecimals, string TimeZone)
{
    public static CountryView From(Tas.Application.Countries.CountryPack p) =>
        new(p.Code, p.NameEn, p.NameAr, "+" + p.Mobile.DialCode, p.Mobile.Example, p.IdNameEn, p.IdNameAr, p.Currency, p.CurrencyDecimals, p.TimeZone);
}

/// <summary>Companies, trucks, drivers, categories, cargo releases and users.</summary>
public static class MasterDataEndpoints
{
    public static WebApplication MapMasterData(this WebApplication app)
    {
        // ================================================================ companies & fleet

        var companies = app.MapGroup("/api/companies").RequireAuthorization();

        companies.MapGet("/", async (string? search, HttpContext http, FleetService service, CancellationToken ct) =>
            Results.Ok(await service.ListCompaniesAsync(TasApi.User(http)!, search, ct)));

        companies.MapPost("/", async (CompanyInput body, HttpContext http, FleetService service, CancellationToken ct) =>
            !AccessRules.CanManageCompanies(TasApi.User(http)!)
                ? TasApi.Forbidden("Only terminal admins can register companies.")
                : TasApi.Admin(await service.CreateCompanyAsync(TasApi.User(http)!, body, ct), c => $"/api/companies/{c.CompanyId}"));

        companies.MapGet("/{companyId:int}", async (int companyId, HttpContext http, FleetService service, CancellationToken ct) =>
        {
            if (!AccessRules.CanViewCompany(TasApi.User(http)!, companyId))
                return NotFound(companyId);
            var company = await service.GetCompanyAsync(companyId, ct);
            return company is null ? NotFound(companyId) : Results.Ok(company);
        });

        companies.MapPut("/{companyId:int}", async (int companyId, CompanyInput body, HttpContext http, FleetService service, CancellationToken ct) =>
            !AccessRules.CanManageCompanies(TasApi.User(http)!)
                ? TasApi.Forbidden("Only terminal admins can change company details.")
                : TasApi.Admin(await service.UpdateCompanyAsync(TasApi.User(http)!, companyId, body, ct)));

        companies.MapPost("/{companyId:int}/status", async (int companyId, CompanyStatusInput body, HttpContext http, FleetService service, CancellationToken ct) =>
            !AccessRules.CanManageCompanies(TasApi.User(http)!)
                ? TasApi.Forbidden("Only terminal admins can activate or suspend companies.")
                : TasApi.Admin(await service.SetCompanyStatusAsync(TasApi.User(http)!, companyId, body, ct)));

        // ---------------------------------------------------------------- trucks

        companies.MapGet("/{companyId:int}/trucks", async (int companyId, HttpContext http, FleetService service, CancellationToken ct) =>
            AccessRules.CanViewCompany(TasApi.User(http)!, companyId)
                ? Results.Ok(await service.ListTrucksAsync(companyId, ct))
                : NotFound(companyId));

        companies.MapPost("/{companyId:int}/trucks", async (int companyId, TruckInput body, HttpContext http, FleetService service, CancellationToken ct) =>
            FleetDenied(http, companyId)
            ?? TasApi.Admin(await service.CreateTruckAsync(TasApi.User(http)!, companyId, body, ct), t => $"/api/companies/{companyId}/trucks/{t.TruckId}"));

        companies.MapPut("/{companyId:int}/trucks/{truckId:int}", async (
            int companyId, int truckId, TruckInput body, HttpContext http, FleetService service, CancellationToken ct) =>
            FleetDenied(http, companyId)
            ?? TasApi.Admin(await service.UpdateTruckAsync(TasApi.User(http)!, companyId, truckId, body, ct)));

        companies.MapPost("/{companyId:int}/trucks/{truckId:int}/block", async (
            int companyId, int truckId, BlockInput body, HttpContext http, FleetService service, CancellationToken ct) =>
            !AccessRules.CanBlockFleet(TasApi.User(http)!)
                ? TasApi.Forbidden("Only terminal admins can block or unblock trucks.")
                : TasApi.Admin(await service.SetTruckBlockedAsync(TasApi.User(http)!, companyId, truckId, body, ct)));

        // ---------------------------------------------------------------- drivers

        companies.MapGet("/{companyId:int}/drivers", async (int companyId, HttpContext http, FleetService service, CancellationToken ct) =>
            AccessRules.CanViewCompany(TasApi.User(http)!, companyId)
                ? Results.Ok(await service.ListDriversAsync(companyId, ct))
                : NotFound(companyId));

        companies.MapPost("/{companyId:int}/drivers", async (int companyId, DriverInput body, HttpContext http, FleetService service, CancellationToken ct) =>
            FleetDenied(http, companyId)
            ?? TasApi.Admin(await service.CreateDriverAsync(TasApi.User(http)!, companyId, body, ct), d => $"/api/companies/{companyId}/drivers/{d.DriverId}"));

        companies.MapPut("/{companyId:int}/drivers/{driverId:int}", async (
            int companyId, int driverId, DriverInput body, HttpContext http, FleetService service, CancellationToken ct) =>
            FleetDenied(http, companyId)
            ?? TasApi.Admin(await service.UpdateDriverAsync(TasApi.User(http)!, companyId, driverId, body, ct)));

        companies.MapPost("/{companyId:int}/drivers/{driverId:int}/block", async (
            int companyId, int driverId, BlockInput body, HttpContext http, FleetService service, CancellationToken ct) =>
            !AccessRules.CanBlockFleet(TasApi.User(http)!)
                ? TasApi.Forbidden("Only terminal admins can block or unblock drivers.")
                : TasApi.Admin(await service.SetDriverBlockedAsync(TasApi.User(http)!, companyId, driverId, body, ct)));

        // Personal data (Saudi PDPL): a copy of everything held about a driver, and erasure on request.
        companies.MapGet("/{companyId:int}/drivers/{driverId:int}/personal-data", async (
            int companyId, int driverId, HttpContext http, Tas.Infrastructure.Ops.PersonalDataService service, CancellationToken ct) =>
        {
            var result = await service.ExportAsync(TasApi.User(http)!, companyId, driverId, ct);
            if (result.Status != Tas.Application.Capacity.AdminStatus.Ok)
                return TasApi.Admin(result);
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(result.Value);
        });

        companies.MapPost("/{companyId:int}/drivers/{driverId:int}/erase", async (
            int companyId, int driverId, EraseBody body, HttpContext http, Tas.Infrastructure.Ops.PersonalDataService service, CancellationToken ct) =>
            TasApi.Admin(await service.EraseAsync(TasApi.User(http)!, companyId, driverId, body.Reason, ct)));

        // ================================================================ terminals

        // The terminals the signed-in user can work at (the portal's terminal picker).
        // Companies and system admins see every active terminal; terminal staff see their own; operator admins their operator's.
        // The installation's country: how mobiles are typed, the drivers' ID document (public: the drivers' app shows it before sign-in).
        app.MapGet("/api/country", (Tas.Infrastructure.TerminalProfiles profiles) => Results.Ok(CountryView.From(profiles.InstallationCountry)))
            .AllowAnonymous();

        // Languages: the built-in ones and the installation's language packs (public: the sign-in page and the app need them).
        app.MapGet("/api/languages", () => Results.Ok(Tas.Application.Languages.LanguageCatalog.All)).AllowAnonymous();

        // A pack's texts for the portal and the app (the built-in languages' texts ship with the portal and the app).
        app.MapGet("/api/languages/{code}", (string code) =>
            Tas.Application.Languages.LanguageCatalog.Pack(code) is { } pack
                ? Results.Ok(new { pack.Code, pack.Name, pack.Direction, pack.Portal, pack.App })
                : TasApi.Problem(StatusCodes.Status404NotFound, $"No language pack '{code}'.")).AllowAnonymous();

        // The terminal's public holidays in a year (lunar ones are expected dates: they follow the moon sighting).
        app.MapGet("/api/terminals/{terminalId:int}/holidays", async (
            int terminalId, int? year, HttpContext http, TasDbContext db, Tas.Infrastructure.TerminalProfiles profiles, CancellationToken ct) =>
        {
            if (!AccessRules.CanAccessTerminal(TasApi.User(http)!, terminalId))
                return TasApi.Forbidden("You cannot see this terminal.");
            var code = await Tas.Infrastructure.TerminalProfiles.CodeAsync(db, terminalId, ct);
            if (code is null)
                return TasApi.Problem(StatusCodes.Status404NotFound, $"Terminal {terminalId} not found.");
            var y = year ?? DateTime.UtcNow.Year;
            if (y is < 2000 or > 2075)
                return TasApi.Problem(StatusCodes.Status400BadRequest, "Year must be between 2000 and 2075.");
            return Results.Ok(profiles.CountryFor(code).Holidays(y));
        }).RequireAuthorization();

        app.MapGet("/api/terminals", async (HttpContext http, TasDbContext db, Tas.Infrastructure.TerminalProfiles profiles, CancellationToken ct) =>
        {
            var user = TasApi.User(http)!;
            var query = db.Terminals.AsNoTracking().Where(t => t.IsActive);
            if (user.IsOperatorAdmin)
            {
                var mine = user.OperatorTerminals ?? [];
                query = query.Where(t => mine.Contains(t.TerminalId));
            }
            else if (user.IsTerminalStaff && !user.IsSysAdmin)
                query = query.Where(t => t.TerminalId == user.TerminalId);

            var terminals = await query
                .OrderBy(t => t.Code)
                .Select(t => new TerminalView(t.TerminalId, t.Code, t.Name, t.TerminalType, t.TimeZone, t.SlotMinutes, t.BookingHorizonDays, t.OperatorId, "SA", "SAR"))
                .ToListAsync(ct);
            return Results.Ok(terminals.Select(t =>
            {
                var country = profiles.CountryFor(t.Code);
                return t with { Country = country.Code, Currency = country.Currency };
            }));
        }).RequireAuthorization();

        // ================================================================ operators (system admins; operator admins see their own)

        var operators = app.MapGroup("/api/operators").RequireAuthorization();

        operators.MapGet("/", async (HttpContext http, OperatorService service, CancellationToken ct) =>
        {
            var result = await service.ListAsync(TasApi.User(http)!, ct);
            return result.Status == AdminStatus.Ok ? Results.Ok(result.Value) : TasApi.Admin(result);
        });

        operators.MapPost("/", async (OperatorInput body, HttpContext http, OperatorService service, CancellationToken ct) =>
            TasApi.Admin(await service.CreateAsync(TasApi.User(http)!, body, ct), o => $"/api/operators/{o.OperatorId}"));

        operators.MapPut("/{operatorId:int}", async (int operatorId, OperatorUpdate body, HttpContext http, OperatorService service, CancellationToken ct) =>
            TasApi.Admin(await service.UpdateAsync(TasApi.User(http)!, operatorId, body, ct)));

        // Put a terminal in an operator, move it, or take it out ({ "operatorId": null }).
        app.MapPut("/api/terminals/{terminalId:int}/operator", async (
            int terminalId, TerminalOperatorBody body, HttpContext http, OperatorService service, CancellationToken ct) =>
            TasApi.Admin(await service.AssignTerminalAsync(TasApi.User(http)!, terminalId, body.OperatorId, ct))).RequireAuthorization();

        // ================================================================ terminal: categories & cargo

        var terminal = app.MapGroup("/api/terminals/{terminalId:int}").RequireAuthorization();

        // Everyone who can book at the terminal needs the category list.
        terminal.MapGet("/categories", async (int terminalId, bool? includeInactive, HttpContext http, TerminalDataService service, CancellationToken ct) =>
            AccessRules.CanAccessTerminal(TasApi.User(http)!, terminalId)
                ? Results.Ok(await service.ListCategoriesAsync(terminalId, includeInactive ?? false, ct))
                : TasApi.Forbidden("You have no access to this terminal."));

        terminal.MapPost("/categories", async (int terminalId, CategoryInput body, HttpContext http, TerminalDataService service, CancellationToken ct) =>
            !AccessRules.CanManageCategories(TasApi.User(http)!, terminalId)
                ? TasApi.Forbidden("Only terminal admins of this terminal can define categories.")
                : TasApi.Admin(await service.CreateCategoryAsync(TasApi.User(http)!, terminalId, body, ct), c => $"/api/terminals/{terminalId}/categories/{c.CategoryId}"));

        terminal.MapPut("/categories/{categoryId:int}", async (
            int terminalId, int categoryId, CategoryInput body, HttpContext http, TerminalDataService service, CancellationToken ct) =>
            !AccessRules.CanManageCategories(TasApi.User(http)!, terminalId)
                ? TasApi.Forbidden("Only terminal admins of this terminal can change categories.")
                : TasApi.Admin(await service.UpdateCategoryAsync(TasApi.User(http)!, terminalId, categoryId, body, ct)));

        terminal.MapGet("/cargo", async (int terminalId, CargoStatus? status, HttpContext http, TerminalDataService service, CancellationToken ct) =>
            AccessRules.CanAccessTerminal(TasApi.User(http)!, terminalId)
                ? Results.Ok(await service.ListCargoAsync(terminalId, TasApi.User(http)!, status, ct))
                : TasApi.Forbidden("You have no access to this terminal."));

        terminal.MapPost("/cargo", async (int terminalId, CargoInput body, HttpContext http, TerminalDataService service, CancellationToken ct) =>
            !AccessRules.CanManageCargo(TasApi.User(http)!, terminalId)
                ? TasApi.Forbidden("Only planners and terminal admins of this terminal can record cargo releases.")
                : TasApi.Admin(await service.CreateCargoAsync(TasApi.User(http)!, terminalId, body, ct), c => $"/api/terminals/{terminalId}/cargo/{c.CargoAllocationId}"));

        terminal.MapPut("/cargo/{cargoId:int}", async (
            int terminalId, int cargoId, CargoInput body, HttpContext http, TerminalDataService service, CancellationToken ct) =>
            !AccessRules.CanManageCargo(TasApi.User(http)!, terminalId)
                ? TasApi.Forbidden("Only planners and terminal admins of this terminal can change cargo releases.")
                : TasApi.Admin(await service.UpdateCargoAsync(TasApi.User(http)!, terminalId, cargoId, body, ct)));

        // ================================================================ users (create is in Program.cs)

        var users = app.MapGroup("/api/users").RequireAuthorization();

        users.MapGet("/", async (int? companyId, int? terminalId, int? operatorId, HttpContext http, UserAdminService service, CancellationToken ct) =>
        {
            var result = await service.ListAsync(TasApi.User(http)!, companyId, terminalId, ct, operatorId);
            return result.Status == AdminStatus.Ok ? Results.Ok(result.Value) : TasApi.Admin(result);
        });

        users.MapPut("/{userId:int}", async (int userId, UpdateUserInput body, HttpContext http, UserAdminService service, CancellationToken ct) =>
            TasApi.Admin(await service.UpdateAsync(TasApi.User(http)!, userId, body, ct)));

        users.MapPost("/{userId:int}/active", async (int userId, ActiveBody body, HttpContext http, UserAdminService service, CancellationToken ct) =>
            TasApi.Admin(await service.SetActiveAsync(TasApi.User(http)!, userId, body.Active, body.Reason, ct)));

        // Email the user a link to set their password (invitation, or reset when they have one).
        users.MapPost("/{userId:int}/password-link", async (int userId, HttpContext http, UserAdminService service, CancellationToken ct) =>
        {
            var result = await service.SendPasswordLinkAsync(TasApi.User(http)!, userId, TasApi.Origin(http), ct);
            if (result.Value is { } token)
                http.Response.Headers["X-Yardsyncs-Test-Token"] = token;
            return result.Status == AdminStatus.Ok ? Results.Accepted() : TasApi.Admin(result);
        });

        users.MapPost("/{userId:int}/reset-password", async (
            int userId, ResetPasswordInput body, HttpContext http, UserAdminService service, CancellationToken ct) =>
            TasApi.Admin(await service.ResetPasswordAsync(TasApi.User(http)!, userId, body.NewPassword, ct)));

        return app;
    }

    // Other companies' records look like they don't exist.
    private static IResult NotFound(int companyId) => TasApi.Problem(StatusCodes.Status404NotFound, $"Company {companyId} not found.");

    private static IResult? FleetDenied(HttpContext http, int companyId)
    {
        var user = TasApi.User(http)!;
        if (AccessRules.CanManageFleet(user, companyId))
            return null;
        return AccessRules.CanViewCompany(user, companyId)
            ? TasApi.Forbidden("Only the company admin or a terminal admin can change the fleet.")
            : NotFound(companyId);
    }
}
