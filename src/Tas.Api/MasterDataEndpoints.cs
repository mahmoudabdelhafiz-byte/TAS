using Tas.Application.Auth;
using Tas.Application.Capacity;
using Tas.Application.MasterData;
using Tas.Domain.Booking;
using Tas.Infrastructure.Auth;
using Tas.Infrastructure.MasterData;

namespace Tas.Api;

public sealed record ActiveBody(bool Active, string? Reason);

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

        users.MapGet("/", async (int? companyId, int? terminalId, HttpContext http, UserAdminService service, CancellationToken ct) =>
        {
            var result = await service.ListAsync(TasApi.User(http)!, companyId, terminalId, ct);
            return result.Status == AdminStatus.Ok ? Results.Ok(result.Value) : TasApi.Admin(result);
        });

        users.MapPut("/{userId:int}", async (int userId, UpdateUserInput body, HttpContext http, UserAdminService service, CancellationToken ct) =>
            TasApi.Admin(await service.UpdateAsync(TasApi.User(http)!, userId, body, ct)));

        users.MapPost("/{userId:int}/active", async (int userId, ActiveBody body, HttpContext http, UserAdminService service, CancellationToken ct) =>
            TasApi.Admin(await service.SetActiveAsync(TasApi.User(http)!, userId, body.Active, body.Reason, ct)));

        users.MapPost("/{userId:int}/reset-password", async (
            int userId, ResetPasswordInput body, HttpContext http, UserAdminService service, CancellationToken ct) =>
            TasApi.Admin(await service.ResetPasswordAsync(TasApi.User(http)!, userId, body.NewPassword, ct)));

        // New invitation link for a user who hasn't set a password yet (the previous link stops working).
        users.MapPost("/{userId:int}/invite", async (int userId, HttpContext http, UserAdminService service, CancellationToken ct) =>
            TasApi.Admin(await service.ResendInviteAsync(TasApi.User(http)!, userId, ct)));

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
