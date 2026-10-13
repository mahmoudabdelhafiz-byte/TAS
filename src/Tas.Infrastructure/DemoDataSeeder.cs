using Microsoft.EntityFrameworkCore;
using Tas.Domain;
using Tas.Domain.Booking;
using Tas.Domain.Capacity;
using Tas.Domain.Parties;
using Tas.Infrastructure.Capacity;
using Tas.Infrastructure.Persistence;

namespace Tas.Infrastructure;

public sealed record DemoSeedResult(
    bool Created,
    int TerminalId,
    string TerminalCode,
    int CompanyId,
    int DispatcherUserId,
    int PlannerUserId,
    IReadOnlyList<int> TruckIds,
    IReadOnlyList<int> DriverIds);

/// <summary>
/// A ready-to-demo multipurpose terminal: categories, a 24/7 weekday template, Fri/Sat hours,
/// quotas, a walk-in reserve, a trucking company with users/trucks/drivers and two cargo releases.
/// Pair with the FakeTos section of appsettings.Development.json. Development only.
/// </summary>
public sealed class DemoDataSeeder(TasDbContext db, SlotGenerationService slotGeneration, TimeProvider clock)
{
    public const string TerminalCode = "DEMO";
    private const string DispatcherEmail = "dispatcher@alnoor.example";
    private const string PlannerEmail = "planner@demo-terminal.example";

    public async Task<DemoSeedResult> SeedAsync(CancellationToken ct = default)
    {
        var existing = await db.Terminals.AsNoTracking().SingleOrDefaultAsync(t => t.Code == TerminalCode, ct);
        if (existing is not null)
            return await DescribeAsync(existing.TerminalId, created: false, ct);

        var terminal = new Terminal
        {
            Code = TerminalCode,
            Name = "Demo Multipurpose Terminal (Jeddah)",
            TerminalType = TerminalType.Multipurpose,
            TimeZone = "Asia/Riyadh",
            SlotMinutes = 60,
            BookingHorizonDays = 14,
        };
        db.Terminals.Add(terminal);
        await db.SaveChangesAsync(ct);
        var tid = terminal.TerminalId;

        var categories = new[]
        {
            Category(tid, "IMP_PICKUP", "Import pickup", "استلام حاوية وارد", CategoryMode.Container, 1),
            Category(tid, "EXP_DROP", "Export drop-off", "تسليم حاوية صادر", CategoryMode.Container, 2),
            Category(tid, "MTY_RETURN", "Empty return", "إرجاع حاوية فارغة", CategoryMode.Container, 3),
            Category(tid, "MTY_PICKUP", "Empty pickup", "استلام حاوية فارغة", CategoryMode.Container, 4),
            Category(tid, "BB_DELIVERY", "Breakbulk delivery", "استلام بضائع عامة", CategoryMode.Cargo, 5, UnitType.Trip),
        };
        db.TransactionCategories.AddRange(categories);
        await db.SaveChangesAsync(ct);
        var ids = categories.ToDictionary(c => c.Code, c => c.CategoryId);

        List<CapacityTemplateQuota> WeekdayQuotas() =>
        [
            new() { CategoryId = ids["IMP_PICKUP"], MaxUnits = 15 },
            new() { CategoryId = ids["EXP_DROP"], MaxUnits = 12 },
            new() { CategoryId = ids["MTY_RETURN"], MaxUnits = 8 },
            new() { CategoryId = ids["MTY_PICKUP"], MaxUnits = 6 },
            new() { CategoryId = ids["BB_DELIVERY"], MaxUnits = 5 },
        ];

        var template = new CapacityTemplate { TerminalId = tid, Name = "Default", IsDefault = true };
        foreach (var day in new[] { DayOfWeek.Sunday, DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday })
        {
            template.Slots.Add(new CapacityTemplateSlot
            {
                Day = day, StartTime = new TimeOnly(0, 0), EndTime = new TimeOnly(23, 59),
                TotalCapacity = 30, OverbookPct = 5, Quotas = WeekdayQuotas(),
            });
        }
        foreach (var day in new[] { DayOfWeek.Friday, DayOfWeek.Saturday })
        {
            template.Slots.Add(new CapacityTemplateSlot
            {
                Day = day, StartTime = new TimeOnly(6, 0), EndTime = new TimeOnly(22, 0), TotalCapacity = 20,
                Quotas = [new() { CategoryId = ids["BB_DELIVERY"], MaxUnits = 0 }], // no breakbulk on the weekend
            });
        }
        db.CapacityTemplates.Add(template);

        db.TerminalSettings.AddRange(
            new TerminalSetting { TerminalId = tid, SettingKey = TerminalSettings.EarlyGraceKey, SettingValue = "30" },
            new TerminalSetting { TerminalId = tid, SettingKey = TerminalSettings.LateGraceKey, SettingValue = "30" },
            new TerminalSetting { TerminalId = tid, SettingKey = TerminalSettings.CancelCutoffKey, SettingValue = "60" },
            new TerminalSetting { TerminalId = tid, SettingKey = TerminalSettings.MaxPerTruckPerDayKey, SettingValue = "6" });

        db.QuotaReleaseRules.Add(new QuotaReleaseRule { TerminalId = tid, CategoryId = ids["MTY_RETURN"], HoursBefore = 12 });

        var now = clock.GetUtcNow();
        db.CapacityReservations.Add(new CapacityReservation
        {
            TerminalId = tid, StartAt = now.AddDays(-1), EndAt = now.AddYears(1), UnitsPerSlot = 2, ReleaseHoursBefore = 2,
        });

        var company = new Company { Name = "Al Noor Transport", CrNumber = "4030123456", CompanyType = CompanyType.Trucking };
        db.Companies.Add(company);
        await db.SaveChangesAsync(ct);

        db.AppUsers.AddRange(
            new AppUser
            {
                CompanyId = company.CompanyId, Email = DispatcherEmail, FullName = "Khalid (Dispatcher)", Role = UserRole.CompanyAdmin,
            },
            new AppUser
            {
                TerminalId = tid, Email = PlannerEmail, FullName = "Terminal Planner", Role = UserRole.Planner,
            });

        db.Trucks.AddRange(
            new Truck { CompanyId = company.CompanyId, PlateNumber = "1234 ABJ", PlateNormalized = "1234ABJ" },
            new Truck { CompanyId = company.CompanyId, PlateNumber = "5678 HDR", PlateNormalized = "5678HDR" });

        db.Drivers.AddRange(
            new Driver { CompanyId = company.CompanyId, FullName = "Abdullah Al-Harbi", IdNumber = "1012345678", Mobile = "+966500000001", PreferredLanguage = "ar" },
            new Driver { CompanyId = company.CompanyId, FullName = "Mohammed Khan", IdNumber = "2012345678", Mobile = "+966500000002", PreferredLanguage = "ur" });

        db.CargoAllocations.AddRange(
            new CargoAllocation
            {
                TerminalId = tid, CompanyId = company.CompanyId, CargoRef = "BL-STEEL-001", Commodity = "Steel coils",
                CargoType = CargoType.Breakbulk, TotalTonnage = 1200, TotalTrips = 40,
            },
            new CargoAllocation
            {
                TerminalId = tid, CargoRef = "BL-PIPES-002", Commodity = "Steel pipes",
                CargoType = CargoType.Breakbulk, TotalTonnage = 300, TotalTrips = 10,
            });

        await db.SaveChangesAsync(ct);

        await slotGeneration.GenerateAllAsync(ct);
        return await DescribeAsync(tid, created: true, ct);
    }

    private async Task<DemoSeedResult> DescribeAsync(int terminalId, bool created, CancellationToken ct)
    {
        var dispatcher = await db.AppUsers.AsNoTracking().SingleAsync(u => u.Email == DispatcherEmail, ct);
        var planner = await db.AppUsers.AsNoTracking().SingleAsync(u => u.Email == PlannerEmail, ct);
        var companyId = dispatcher.CompanyId!.Value;
        var trucks = await db.Trucks.AsNoTracking().Where(t => t.CompanyId == companyId).OrderBy(t => t.TruckId).Select(t => t.TruckId).ToListAsync(ct);
        var drivers = await db.Drivers.AsNoTracking().Where(d => d.CompanyId == companyId).OrderBy(d => d.DriverId).Select(d => d.DriverId).ToListAsync(ct);
        return new DemoSeedResult(created, terminalId, TerminalCode, companyId, dispatcher.UserId, planner.UserId, trucks, drivers);
    }

    private static TransactionCategory Category(
        int terminalId, string code, string en, string ar, CategoryMode mode, int order, UnitType unit = UnitType.Move) =>
        new() { TerminalId = terminalId, Code = code, NameEn = en, NameAr = ar, Mode = mode, SortOrder = order, UnitType = unit };
}
