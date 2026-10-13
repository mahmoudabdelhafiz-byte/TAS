using Microsoft.EntityFrameworkCore;
using Npgsql;
using Tas.Application.Auth;
using Tas.Application.Capacity;
using Tas.Application.MasterData;
using Tas.Domain;
using Tas.Domain.Parties;
using Tas.Infrastructure.Persistence;

namespace Tas.Infrastructure.MasterData;

/// <summary>
/// Companies and their fleet (trucks, drivers). Permissions are checked by the endpoints with
/// AccessRules (CanManageCompanies, CanViewCompany, CanManageFleet, CanBlockFleet).
/// </summary>
public sealed class FleetService(TasDbContext db, TimeProvider clock)
{
    // ================================================================ companies

    public async Task<IReadOnlyList<CompanyView>> ListCompaniesAsync(CurrentUser user, string? search, CancellationToken ct = default)
    {
        var query = db.Companies.AsNoTracking();
        if (user.IsCompanyUser)
            query = query.Where(c => c.CompanyId == user.CompanyId);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search.Trim().Replace("%", "").Replace("_", "")}%";
            query = query.Where(c => EF.Functions.ILike(c.Name, pattern) || (c.CrNumber != null && EF.Functions.ILike(c.CrNumber, pattern)));
        }

        var companies = await query.OrderBy(c => c.Name).Take(500).ToListAsync(ct);
        return companies.Select(CompanyView.From).ToList();
    }

    public async Task<CompanyView?> GetCompanyAsync(int companyId, CancellationToken ct = default)
    {
        var company = await db.Companies.AsNoTracking().SingleOrDefaultAsync(c => c.CompanyId == companyId, ct);
        return company is null ? null : CompanyView.From(company);
    }

    public async Task<AdminResult<CompanyView>> CreateCompanyAsync(CurrentUser actor, CompanyInput input, CancellationToken ct = default)
    {
        var errors = MasterDataValidator.Company(input);
        if (errors.Count > 0)
            return AdminResult<CompanyView>.Invalid(errors);

        var cr = Clean(input.CrNumber);
        if (cr is not null && await db.Companies.AnyAsync(c => c.CrNumber == cr, ct))
            return AdminResult<CompanyView>.Conflict($"A company with CR {cr} already exists.");

        var company = new Company
        {
            Name = input.Name.Trim(),
            CrNumber = cr,
            CompanyType = input.CompanyType,
            IsPriority = input.IsPriority,
            Status = CompanyStatus.Active,
        };

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.Companies.Add(company);
        await db.SaveChangesAsync(ct);
        Audit("company", company.CompanyId, AuditAction.Insert, null, CompanyView.From(company), null, actor);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return AdminResult<CompanyView>.Created(CompanyView.From(company));
    }

    public async Task<AdminResult<CompanyView>> UpdateCompanyAsync(CurrentUser actor, int companyId, CompanyInput input, CancellationToken ct = default)
    {
        var company = await db.Companies.SingleOrDefaultAsync(c => c.CompanyId == companyId, ct);
        if (company is null)
            return AdminResult<CompanyView>.NotFound($"Company {companyId} not found.");

        var errors = MasterDataValidator.Company(input);
        if (errors.Count > 0)
            return AdminResult<CompanyView>.Invalid(errors);

        var cr = Clean(input.CrNumber);
        if (cr is not null && await db.Companies.AnyAsync(c => c.CrNumber == cr && c.CompanyId != companyId, ct))
            return AdminResult<CompanyView>.Conflict($"A company with CR {cr} already exists.");

        var before = CompanyView.From(company);
        company.Name = input.Name.Trim();
        company.CrNumber = cr;
        company.CompanyType = input.CompanyType;
        company.IsPriority = input.IsPriority;
        Audit("company", company.CompanyId, AuditAction.Update, before, CompanyView.From(company), null, actor);
        await db.SaveChangesAsync(ct);
        return AdminResult<CompanyView>.Ok(CompanyView.From(company));
    }

    /// <summary>Suspended companies cannot book; their existing appointments stay.</summary>
    public async Task<AdminResult<CompanyView>> SetCompanyStatusAsync(
        CurrentUser actor, int companyId, CompanyStatusInput input, CancellationToken ct = default)
    {
        var company = await db.Companies.SingleOrDefaultAsync(c => c.CompanyId == companyId, ct);
        if (company is null)
            return AdminResult<CompanyView>.NotFound($"Company {companyId} not found.");
        if (!Enum.IsDefined(input.Status))
            return AdminResult<CompanyView>.Invalid("Status must be Pending, Active or Suspended.");
        if (input.Status == CompanyStatus.Suspended && string.IsNullOrWhiteSpace(input.Reason))
            return AdminResult<CompanyView>.Invalid("A reason is required to suspend a company.");

        if (company.Status != input.Status)
        {
            var before = CompanyView.From(company);
            company.Status = input.Status;
            Audit("company", company.CompanyId, AuditAction.Update, before, CompanyView.From(company), input.Reason, actor);
            await db.SaveChangesAsync(ct);
        }
        return AdminResult<CompanyView>.Ok(CompanyView.From(company));
    }

    // ================================================================ trucks

    public async Task<IReadOnlyList<TruckView>> ListTrucksAsync(int companyId, CancellationToken ct = default) =>
        (await db.Trucks.AsNoTracking().Where(t => t.CompanyId == companyId).OrderBy(t => t.PlateNormalized).ToListAsync(ct))
        .Select(TruckView.From).ToList();

    public async Task<AdminResult<TruckView>> CreateTruckAsync(CurrentUser actor, int companyId, TruckInput input, CancellationToken ct = default)
    {
        if (!await db.Companies.AnyAsync(c => c.CompanyId == companyId, ct))
            return AdminResult<TruckView>.NotFound($"Company {companyId} not found.");

        var (plate, error) = MasterDataValidator.Truck(input);
        if (plate is null)
            return AdminResult<TruckView>.Invalid(error!);
        if (await PlateTakenAsync(plate, null, ct) is { } conflict)
            return conflict;

        var truck = new Truck { CompanyId = companyId, PlateNumber = input.PlateNumber.Trim(), PlateNormalized = plate };
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.Trucks.Add(truck);
        if (!await TrySaveAsync(ct))
            return AdminResult<TruckView>.Conflict($"Plate {plate} is already registered.");
        Audit("truck", truck.TruckId, AuditAction.Insert, null, TruckView.From(truck), null, actor);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return AdminResult<TruckView>.Created(TruckView.From(truck));
    }

    public async Task<AdminResult<TruckView>> UpdateTruckAsync(
        CurrentUser actor, int companyId, int truckId, TruckInput input, CancellationToken ct = default)
    {
        var truck = await db.Trucks.SingleOrDefaultAsync(t => t.TruckId == truckId && t.CompanyId == companyId, ct);
        if (truck is null)
            return AdminResult<TruckView>.NotFound($"Truck {truckId} not found.");

        var (plate, error) = MasterDataValidator.Truck(input);
        if (plate is null)
            return AdminResult<TruckView>.Invalid(error!);
        if (await PlateTakenAsync(plate, truckId, ct) is { } conflict)
            return conflict;

        var before = TruckView.From(truck);
        truck.PlateNumber = input.PlateNumber.Trim();
        truck.PlateNormalized = plate;
        Audit("truck", truck.TruckId, AuditAction.Update, before, TruckView.From(truck), null, actor);
        return await TrySaveAsync(ct)
            ? AdminResult<TruckView>.Ok(TruckView.From(truck))
            : AdminResult<TruckView>.Conflict($"Plate {plate} is already registered.");
    }

    public async Task<AdminResult<TruckView>> SetTruckBlockedAsync(
        CurrentUser actor, int companyId, int truckId, BlockInput input, CancellationToken ct = default)
    {
        var truck = await db.Trucks.SingleOrDefaultAsync(t => t.TruckId == truckId && t.CompanyId == companyId, ct);
        if (truck is null)
            return AdminResult<TruckView>.NotFound($"Truck {truckId} not found.");
        if (input.Blocked && string.IsNullOrWhiteSpace(input.Reason))
            return AdminResult<TruckView>.Invalid("A reason is required to block a truck.");

        if (truck.IsBlocked != input.Blocked)
        {
            var before = TruckView.From(truck);
            truck.IsBlocked = input.Blocked;
            Audit("truck", truck.TruckId, AuditAction.Update, before, TruckView.From(truck), input.Reason, actor);
            await db.SaveChangesAsync(ct);
        }
        return AdminResult<TruckView>.Ok(TruckView.From(truck));
    }

    // ================================================================ drivers

    public async Task<IReadOnlyList<DriverView>> ListDriversAsync(int companyId, CancellationToken ct = default) =>
        (await db.Drivers.AsNoTracking().Where(d => d.CompanyId == companyId).OrderBy(d => d.FullName).ToListAsync(ct))
        .Select(DriverView.From).ToList();

    public async Task<AdminResult<DriverView>> CreateDriverAsync(CurrentUser actor, int companyId, DriverInput input, CancellationToken ct = default)
    {
        if (!await db.Companies.AnyAsync(c => c.CompanyId == companyId, ct))
            return AdminResult<DriverView>.NotFound($"Company {companyId} not found.");

        var (mobile, errors) = MasterDataValidator.Driver(input);
        if (errors.Count > 0)
            return AdminResult<DriverView>.Invalid(errors);

        var idNumber = input.IdNumber.Trim().ToUpperInvariant();
        if (await db.Drivers.AnyAsync(d => d.CompanyId == companyId && d.IdNumber == idNumber, ct))
            return AdminResult<DriverView>.Conflict($"A driver with ID {idNumber} is already registered for this company.");

        var driver = new Driver
        {
            CompanyId = companyId,
            FullName = input.FullName.Trim(),
            IdNumber = idNumber,
            Mobile = mobile!,
            PreferredLanguage = input.PreferredLanguage.Trim().ToLowerInvariant(),
        };

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.Drivers.Add(driver);
        if (!await TrySaveAsync(ct))
            return AdminResult<DriverView>.Conflict($"A driver with ID {idNumber} is already registered for this company.");
        Audit("driver", driver.DriverId, AuditAction.Insert, null, DriverView.From(driver), null, actor);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return AdminResult<DriverView>.Created(DriverView.From(driver));
    }

    public async Task<AdminResult<DriverView>> UpdateDriverAsync(
        CurrentUser actor, int companyId, int driverId, DriverInput input, CancellationToken ct = default)
    {
        var driver = await db.Drivers.SingleOrDefaultAsync(d => d.DriverId == driverId && d.CompanyId == companyId, ct);
        if (driver is null)
            return AdminResult<DriverView>.NotFound($"Driver {driverId} not found.");

        var (mobile, errors) = MasterDataValidator.Driver(input);
        if (errors.Count > 0)
            return AdminResult<DriverView>.Invalid(errors);

        var idNumber = input.IdNumber.Trim().ToUpperInvariant();
        if (await db.Drivers.AnyAsync(d => d.CompanyId == companyId && d.IdNumber == idNumber && d.DriverId != driverId, ct))
            return AdminResult<DriverView>.Conflict($"A driver with ID {idNumber} is already registered for this company.");

        var before = DriverView.From(driver);
        driver.FullName = input.FullName.Trim();
        driver.IdNumber = idNumber;
        driver.Mobile = mobile!;
        driver.PreferredLanguage = input.PreferredLanguage.Trim().ToLowerInvariant();
        Audit("driver", driver.DriverId, AuditAction.Update, before, DriverView.From(driver), null, actor);
        return await TrySaveAsync(ct)
            ? AdminResult<DriverView>.Ok(DriverView.From(driver))
            : AdminResult<DriverView>.Conflict($"A driver with ID {idNumber} is already registered for this company.");
    }

    public async Task<AdminResult<DriverView>> SetDriverBlockedAsync(
        CurrentUser actor, int companyId, int driverId, BlockInput input, CancellationToken ct = default)
    {
        var driver = await db.Drivers.SingleOrDefaultAsync(d => d.DriverId == driverId && d.CompanyId == companyId, ct);
        if (driver is null)
            return AdminResult<DriverView>.NotFound($"Driver {driverId} not found.");
        if (input.Blocked && string.IsNullOrWhiteSpace(input.Reason))
            return AdminResult<DriverView>.Invalid("A reason is required to block a driver.");

        if (driver.IsBlocked != input.Blocked)
        {
            var before = DriverView.From(driver);
            driver.IsBlocked = input.Blocked;
            Audit("driver", driver.DriverId, AuditAction.Update, before, DriverView.From(driver), input.Reason, actor);
            await db.SaveChangesAsync(ct);
        }
        return AdminResult<DriverView>.Ok(DriverView.From(driver));
    }

    // ================================================================ helpers

    private async Task<AdminResult<TruckView>?> PlateTakenAsync(string plate, int? exceptTruckId, CancellationToken ct)
    {
        var owner = await db.Trucks.AsNoTracking()
            .Where(t => t.PlateNormalized == plate && t.TruckId != exceptTruckId)
            .Select(t => (int?)t.CompanyId)
            .FirstOrDefaultAsync(ct);
        return owner is null ? null : AdminResult<TruckView>.Conflict($"Plate {plate} is already registered (company {owner}).");
    }

    private async Task<bool> TrySaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.ChangeTracker.Clear();
            return false;
        }
    }

    private void Audit(string entity, int key, AuditAction action, object? before, object? after, string? reason, CurrentUser actor) =>
        AuditWriter.Add(db, null, entity, key, action, before, after, reason, actor.UserId, clock.GetUtcNow());

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
