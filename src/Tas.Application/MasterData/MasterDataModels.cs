using Tas.Domain;
using Tas.Domain.Booking;
using Tas.Domain.Parties;

namespace Tas.Application.MasterData;

// ---------------------------------------------------------------- inputs

public sealed record CompanyInput(string Name, string? CrNumber, CompanyType CompanyType, bool IsPriority = false);

public sealed record CompanyStatusInput(CompanyStatus Status, string? Reason);

public sealed record TruckInput(string PlateNumber);

public sealed record BlockInput(bool Blocked, string? Reason);

public sealed record DriverInput(string FullName, string IdNumber, string Mobile, string PreferredLanguage = "ar");

/// <param name="Code">Upper-case, e.g. IMP_PICKUP. Fixed after creation, as is <paramref name="Mode"/>.</param>
public sealed record CategoryInput(
    string Code,
    string NameEn,
    string NameAr,
    CategoryMode Mode,
    UnitType UnitType = UnitType.Move,
    int SortOrder = 0,
    bool IsActive = true);

public sealed record CargoInput(
    string CargoRef,
    int? CompanyId,
    string? VesselVisitRef,
    string? Commodity,
    CargoType CargoType,
    decimal? TotalTonnage,
    int? TotalTrips,
    CargoStatus Status = CargoStatus.Open);

public sealed record UpdateUserInput(string FullName, string? Mobile, string PreferredLanguage, UserRole Role);

public sealed record ResetPasswordInput(string NewPassword);

// ---------------------------------------------------------------- views

public sealed record CompanyView(
    int CompanyId, string Name, string? CrNumber, CompanyType CompanyType, bool IsPriority, CompanyStatus Status, int NoShowCount, DateTimeOffset CreatedAt)
{
    public static CompanyView From(Company c) =>
        new(c.CompanyId, c.Name, c.CrNumber, c.CompanyType, c.IsPriority, c.Status, c.NoShowCount, c.CreatedAt);
}

public sealed record TruckView(int TruckId, int CompanyId, string PlateNumber, string PlateNormalized, bool IsBlocked)
{
    public static TruckView From(Truck t) => new(t.TruckId, t.CompanyId, t.PlateNumber, t.PlateNormalized, t.IsBlocked);
}

public sealed record DriverView(int DriverId, int CompanyId, string FullName, string IdNumber, string Mobile, string PreferredLanguage, bool IsBlocked)
{
    public static DriverView From(Driver d) =>
        new(d.DriverId, d.CompanyId, d.FullName, d.IdNumber, d.Mobile, d.PreferredLanguage.Trim(), d.IsBlocked);
}

public sealed record CategoryView(
    int CategoryId, string Code, string NameEn, string NameAr, CategoryMode Mode, UnitType UnitType, int SortOrder, bool IsActive)
{
    public static CategoryView From(TransactionCategory c) =>
        new(c.CategoryId, c.Code, c.NameEn, c.NameAr, c.Mode, c.UnitType, c.SortOrder, c.IsActive);
}

public sealed record CargoView(
    int CargoAllocationId, int? CompanyId, string CargoRef, string? VesselVisitRef, string? Commodity, CargoType CargoType,
    decimal? TotalTonnage, int? TotalTrips, decimal DeliveredTonnage, int BookedTrips, CargoStatus Status)
{
    public static CargoView From(CargoAllocation c) =>
        new(c.CargoAllocationId, c.CompanyId, c.CargoRef, c.VesselVisitRef, c.Commodity, c.CargoType,
            c.TotalTonnage, c.TotalTrips, c.DeliveredTonnage, c.BookedTrips, c.Status);
}

public sealed record UserView(
    int UserId, string Email, string FullName, UserRole Role, int? CompanyId, int? TerminalId, string? Mobile,
    string PreferredLanguage, bool IsActive, DateTimeOffset? LastLoginAt, DateTimeOffset? LockedUntil, int? OperatorId = null)
{
    public static UserView From(AppUser u) =>
        new(u.UserId, u.Email, u.FullName, u.Role, u.CompanyId, u.TerminalId, u.Mobile, u.PreferredLanguage.Trim(),
            u.IsActive, u.LastLoginAt, u.LockedUntil, u.OperatorId);
}
