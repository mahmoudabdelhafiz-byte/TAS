using Microsoft.EntityFrameworkCore;
using Npgsql;
using Yardsyncs.Application.Auth;
using Yardsyncs.Application.Capacity;
using Yardsyncs.Application.MasterData;
using Yardsyncs.Domain;
using Yardsyncs.Domain.Booking;
using Yardsyncs.Infrastructure.Capacity;
using Yardsyncs.Infrastructure.Persistence;

namespace Yardsyncs.Infrastructure.MasterData;

/// <summary>
/// Terminal-level master data: transaction categories and multipurpose cargo releases.
/// Endpoints check AccessRules.CanManageCategories / CanManageCargo before the write methods.
/// </summary>
public sealed class TerminalDataService(YardsyncsDbContext db, SlotGenerationService slotGeneration, TimeProvider clock)
{
    // ================================================================ categories

    public async Task<IReadOnlyList<CategoryView>> ListCategoriesAsync(int terminalId, bool includeInactive, CancellationToken ct = default) =>
        (await db.TransactionCategories.AsNoTracking()
            .Where(c => c.TerminalId == terminalId && (includeInactive || c.IsActive))
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Code)
            .ToListAsync(ct))
        .Select(CategoryView.From).ToList();

    /// <summary>Adds a category; slots are regenerated so it can be booked straight away.</summary>
    public async Task<AdminResult<CategoryView>> CreateCategoryAsync(CurrentUser actor, int terminalId, CategoryInput input, CancellationToken ct = default)
    {
        var terminal = await db.Terminals.AsNoTracking().SingleOrDefaultAsync(t => t.TerminalId == terminalId, ct);
        if (terminal is null)
            return AdminResult<CategoryView>.NotFound($"Terminal {terminalId} not found.");

        var errors = MasterDataValidator.Category(input);
        if (errors.Count > 0)
            return AdminResult<CategoryView>.Invalid(errors);

        var code = input.Code.Trim().ToUpperInvariant();
        if (await db.TransactionCategories.AnyAsync(c => c.TerminalId == terminalId && c.Code == code, ct))
            return AdminResult<CategoryView>.Conflict($"Category {code} already exists at this terminal.");

        var category = new TransactionCategory
        {
            TerminalId = terminalId,
            Code = code,
            NameEn = input.NameEn.Trim(),
            NameAr = input.NameAr.Trim(),
            Mode = input.Mode,
            UnitType = input.UnitType,
            SortOrder = input.SortOrder,
            IsActive = input.IsActive,
        };

        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            db.TransactionCategories.Add(category);
            await db.SaveChangesAsync(ct);
            Audit(terminalId, "transaction_category", category.CategoryId, AuditAction.Insert, null, CategoryView.From(category), actor);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        var regenerated = category.IsActive ? await RegenerateHorizonAsync(terminal, ct) : null;
        return AdminResult<CategoryView>.Created(CategoryView.From(category), regenerated);
    }

    /// <summary>Names, unit type, order and active flag can change; code and mode are fixed.</summary>
    public async Task<AdminResult<CategoryView>> UpdateCategoryAsync(
        CurrentUser actor, int terminalId, int categoryId, CategoryInput input, CancellationToken ct = default)
    {
        var terminal = await db.Terminals.AsNoTracking().SingleOrDefaultAsync(t => t.TerminalId == terminalId, ct);
        var category = terminal is null
            ? null
            : await db.TransactionCategories.SingleOrDefaultAsync(c => c.CategoryId == categoryId && c.TerminalId == terminalId, ct);
        if (terminal is null || category is null)
            return AdminResult<CategoryView>.NotFound($"Category {categoryId} not found.");

        var errors = MasterDataValidator.Category(input);
        if (errors.Count > 0)
            return AdminResult<CategoryView>.Invalid(errors);
        if (!string.Equals(input.Code.Trim(), category.Code, StringComparison.OrdinalIgnoreCase) || input.Mode != category.Mode)
            return AdminResult<CategoryView>.Invalid("Code and mode cannot change; create a new category instead.");

        var before = CategoryView.From(category);
        var activeChanged = category.IsActive != input.IsActive;
        category.NameEn = input.NameEn.Trim();
        category.NameAr = input.NameAr.Trim();
        category.UnitType = input.UnitType;
        category.SortOrder = input.SortOrder;
        category.IsActive = input.IsActive;
        Audit(terminalId, "transaction_category", category.CategoryId, AuditAction.Update, before, CategoryView.From(category), actor);
        await db.SaveChangesAsync(ct);

        // Turning a category on/off changes which quota rows the slots have.
        var regenerated = activeChanged ? await RegenerateHorizonAsync(terminal!, ct) : null;
        return AdminResult<CategoryView>.Ok(CategoryView.From(category), regenerated);
    }

    // ================================================================ cargo releases

    /// <summary>Company users see their own company's cargo; terminal staff see all.</summary>
    public async Task<IReadOnlyList<CargoView>> ListCargoAsync(
        int terminalId, CurrentUser user, CargoStatus? status, CancellationToken ct = default)
    {
        var query = db.CargoAllocations.AsNoTracking().Where(c => c.TerminalId == terminalId);
        if (user.IsCompanyUser)
            query = query.Where(c => c.CompanyId == user.CompanyId);
        if (status is not null)
            query = query.Where(c => c.Status == status);

        var cargo = await query.OrderBy(c => c.CargoRef).Take(1000).ToListAsync(ct);
        return cargo.Select(CargoView.From).ToList();
    }

    public async Task<AdminResult<CargoView>> CreateCargoAsync(CurrentUser actor, int terminalId, CargoInput input, CancellationToken ct = default)
    {
        if (!await db.Terminals.AnyAsync(t => t.TerminalId == terminalId, ct))
            return AdminResult<CargoView>.NotFound($"Terminal {terminalId} not found.");

        var errors = MasterDataValidator.Cargo(input);
        if (errors.Count > 0)
            return AdminResult<CargoView>.Invalid(errors);
        if (input.CompanyId is { } companyId && !await db.Companies.AnyAsync(c => c.CompanyId == companyId, ct))
            return AdminResult<CargoView>.Invalid($"Company {companyId} does not exist.");

        var cargoRef = input.CargoRef.Trim().ToUpperInvariant();
        if (await db.CargoAllocations.AnyAsync(c => c.TerminalId == terminalId && c.CargoRef == cargoRef, ct))
            return AdminResult<CargoView>.Conflict($"Cargo {cargoRef} already exists at this terminal.");

        var cargo = new CargoAllocation
        {
            TerminalId = terminalId,
            CompanyId = input.CompanyId,
            CargoRef = cargoRef,
            VesselVisitRef = Clean(input.VesselVisitRef),
            Commodity = Clean(input.Commodity),
            CargoType = input.CargoType,
            TotalTonnage = input.TotalTonnage,
            TotalTrips = input.TotalTrips,
            Status = input.Status,
        };

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.CargoAllocations.Add(cargo);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return AdminResult<CargoView>.Conflict($"Cargo {cargoRef} already exists at this terminal.");
        }
        Audit(terminalId, "cargo_allocation", cargo.CargoAllocationId, AuditAction.Insert, null, CargoView.From(cargo), actor);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return AdminResult<CargoView>.Created(CargoView.From(cargo));
    }

    /// <summary>Everything but the reference can change; trips can't drop below what is already booked.</summary>
    public async Task<AdminResult<CargoView>> UpdateCargoAsync(
        CurrentUser actor, int terminalId, int cargoAllocationId, CargoInput input, CancellationToken ct = default)
    {
        var cargo = await db.CargoAllocations.SingleOrDefaultAsync(c => c.CargoAllocationId == cargoAllocationId && c.TerminalId == terminalId, ct);
        if (cargo is null)
            return AdminResult<CargoView>.NotFound($"Cargo {cargoAllocationId} not found.");

        var errors = MasterDataValidator.Cargo(input).ToList();
        if (!string.Equals(input.CargoRef?.Trim(), cargo.CargoRef, StringComparison.OrdinalIgnoreCase))
            errors.Add("The cargo reference cannot change.");
        if (input.TotalTrips is { } trips && trips < cargo.BookedTrips)
            errors.Add($"Total trips cannot be less than the {cargo.BookedTrips} trips already booked.");
        if (input.CompanyId is { } companyId && !await db.Companies.AnyAsync(c => c.CompanyId == companyId, ct))
            errors.Add($"Company {companyId} does not exist.");
        if (errors.Count > 0)
            return AdminResult<CargoView>.Invalid(errors);

        var before = CargoView.From(cargo);
        cargo.CompanyId = input.CompanyId;
        cargo.VesselVisitRef = Clean(input.VesselVisitRef);
        cargo.Commodity = Clean(input.Commodity);
        cargo.CargoType = input.CargoType;
        cargo.TotalTonnage = input.TotalTonnage;
        cargo.TotalTrips = input.TotalTrips;
        cargo.Status = input.Status;
        Audit(terminalId, "cargo_allocation", cargo.CargoAllocationId, AuditAction.Update, before, CargoView.From(cargo), actor);
        await db.SaveChangesAsync(ct);
        return AdminResult<CargoView>.Ok(CargoView.From(cargo));
    }

    // ================================================================ helpers

    private async Task<SlotGenerationResult> RegenerateHorizonAsync(Terminal terminal, CancellationToken ct)
    {
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(terminal.TimeZone);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), timeZone).DateTime);
        return await slotGeneration.GenerateAsync(terminal.TerminalId, today, today.AddDays(terminal.BookingHorizonDays), ct);
    }

    private void Audit(int terminalId, string entity, int key, AuditAction action, object? before, object? after, CurrentUser actor) =>
        AuditWriter.Add(db, terminalId, entity, key, action, before, after, null, actor.UserId, clock.GetUtcNow());

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
