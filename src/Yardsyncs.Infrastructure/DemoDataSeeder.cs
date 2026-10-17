using Microsoft.EntityFrameworkCore;
using Yardsyncs.Domain;
using Yardsyncs.Domain.Booking;
using Yardsyncs.Domain.Capacity;
using Yardsyncs.Domain.Gate;
using Yardsyncs.Domain.Parties;
using Yardsyncs.Infrastructure.Auth;
using Yardsyncs.Infrastructure.Capacity;
using Yardsyncs.Infrastructure.Persistence;

namespace Yardsyncs.Infrastructure;

public sealed record DemoSeedResult(
    bool Created,
    int TerminalId,
    string TerminalCode,
    int CompanyId,
    int DispatcherUserId,
    int PlannerUserId,
    IReadOnlyList<int> TruckIds,
    IReadOnlyList<int> DriverIds,
    int OtherCompanyId,
    int OtherDispatcherUserId,
    int TerminalAdminUserId,
    IReadOnlyDictionary<string, string> Logins);

/// <summary>
/// A ready-to-demo multipurpose terminal: categories, a 24/7 weekday template, Fri/Sat hours,
/// quotas, a walk-in reserve, a trucking company with users/trucks/drivers and two cargo releases.
/// Pair with the FakeTos section of appsettings.Development.json. Development only.
/// </summary>
public sealed class DemoDataSeeder(YardsyncsDbContext db, SlotGenerationService slotGeneration, PasswordService passwords, TimeProvider clock)
{
    public const string TerminalCode = "DEMO";

    /// <summary>Password of every demo user. Development only.</summary>
    public const string DemoPassword = "Demo-Pass-2026";

    public const string DispatcherEmail = "dispatcher@alnoor.example";
    public const string PlannerEmail = "planner@demo-terminal.example";
    public const string TerminalAdminEmail = "admin@demo-terminal.example";
    public const string OtherDispatcherEmail = "dispatcher@gulfhaulage.example";

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

        var laneIn1 = new GateLane { TerminalId = tid, GateCode = "MAIN", LaneCode = "IN-1", Direction = LaneDirection.In };
        var laneOut1 = new GateLane { TerminalId = tid, GateCode = "MAIN", LaneCode = "OUT-1", Direction = LaneDirection.Out };
        db.GateLanes.AddRange(
            laneIn1,
            new GateLane { TerminalId = tid, GateCode = "MAIN", LaneCode = "IN-2", Direction = LaneDirection.In },
            laneOut1);

        var now = clock.GetUtcNow();
        db.CapacityReservations.Add(new CapacityReservation
        {
            TerminalId = tid, StartAt = now.AddDays(-1), EndAt = now.AddYears(1), UnitsPerSlot = 2, ReleaseHoursBefore = 2,
        });

        var company = new Company { Name = "Al Noor Transport", CrNumber = "4030123456", CompanyType = CompanyType.Trucking };
        var otherCompany = new Company { Name = "Gulf Haulage", CrNumber = "4030654321", CompanyType = CompanyType.Trucking };
        db.Companies.AddRange(company, otherCompany);
        await db.SaveChangesAsync(ct);

        // The Autogate lane model: IN-1 and OUT-1 are equipped (still worked by a clerk), IN-2 is a plain booth lane.
        GateLaneDevice Device(GateLane lane, string code, LaneDeviceKind kind, string? reads = null) =>
            new() { TerminalId = tid, LaneId = lane.LaneId, DeviceCode = code, Kind = kind, Reads = reads, CreatedAt = clock.GetUtcNow() };
        db.LaneDevices.AddRange(
            Device(laneIn1, "IN1-CAM-FRONT", LaneDeviceKind.Camera, "PLATE"),
            Device(laneIn1, "IN1-OCR", LaneDeviceKind.Ocr, "CONTAINER,ISO_CODE,DAMAGE"),
            Device(laneIn1, "IN1-KIOSK", LaneDeviceKind.Kiosk),
            Device(laneIn1, "IN1-BARRIER", LaneDeviceKind.Barrier),
            Device(laneIn1, "IN1-LIGHT", LaneDeviceKind.TrafficLight),
            Device(laneOut1, "OUT1-CAM", LaneDeviceKind.Camera, "PLATE,CONTAINER"),
            Device(laneOut1, "OUT1-BARRIER", LaneDeviceKind.Barrier));
        await db.SaveChangesAsync(ct);

        // Demo terminal areas: approach, pre-gate (checks trucks in) and gate, around 21.470 N 39.170 E.
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO geofence (terminal_id, name, zone_type, area, triggers_check_in) VALUES
              ({tid}, 'Approach roads', 'APPROACH', ST_GeogFromText('POLYGON((39.140 21.440, 39.200 21.440, 39.200 21.500, 39.140 21.500, 39.140 21.440))'), false),
              ({tid}, 'Pre-gate parking', 'PRE_GATE', ST_GeogFromText('POLYGON((39.169 21.471, 39.171 21.471, 39.171 21.4725, 39.169 21.4725, 39.169 21.471))'), true),
              ({tid}, 'Main gate', 'GATE', ST_GeogFromText('POLYGON((39.1695 21.470, 39.1705 21.470, 39.1705 21.471, 39.1695 21.471, 39.1695 21.470))'), false),
              ({tid}, 'Exit gate', 'EXIT', ST_GeogFromText('POLYGON((39.1755 21.4615, 39.1765 21.4615, 39.1765 21.4625, 39.1755 21.4625, 39.1755 21.4615))'), false)
            """, ct);

        // Demo yard: import blocks A1 / B2 north of the middle road, export E1 and empties M1 south of it.
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO geofence (terminal_id, name, zone_type, area, yard_block) VALUES
              ({tid}, 'Block A1', 'YARD_ZONE', ST_GeogFromText('POLYGON((39.165 21.466, 39.168 21.466, 39.168 21.468, 39.165 21.468, 39.165 21.466))'), 'A1'),
              ({tid}, 'Block B2', 'YARD_ZONE', ST_GeogFromText('POLYGON((39.172 21.466, 39.175 21.466, 39.175 21.468, 39.172 21.468, 39.172 21.466))'), 'B2'),
              ({tid}, 'Block E1', 'YARD_ZONE', ST_GeogFromText('POLYGON((39.165 21.463, 39.168 21.463, 39.168 21.465, 39.165 21.465, 39.165 21.463))'), 'E1'),
              ({tid}, 'Block M1', 'YARD_ZONE', ST_GeogFromText('POLYGON((39.172 21.463, 39.175 21.463, 39.175 21.465, 39.172 21.465, 39.172 21.463))'), 'M1')
            """, ct);
        db.TerminalMaps.Add(new TerminalMap { TerminalId = tid, Roads = DemoRoads, UpdatedAt = clock.GetUtcNow() });

        AppUser WithPassword(AppUser user)
        {
            user.PasswordHash = passwords.Hash(user, DemoPassword);
            return user;
        }

        db.AppUsers.AddRange(
            WithPassword(new AppUser
            {
                CompanyId = company.CompanyId, Email = DispatcherEmail, FullName = "Khalid (Al Noor dispatcher)", Role = UserRole.CompanyAdmin,
            }),
            WithPassword(new AppUser
            {
                CompanyId = otherCompany.CompanyId, Email = OtherDispatcherEmail, FullName = "Faisal (Gulf Haulage dispatcher)", Role = UserRole.CompanyAdmin,
            }),
            WithPassword(new AppUser
            {
                TerminalId = tid, Email = PlannerEmail, FullName = "Terminal Planner", Role = UserRole.Planner,
            }),
            WithPassword(new AppUser
            {
                TerminalId = tid, Email = TerminalAdminEmail, FullName = "Terminal Administrator", Role = UserRole.TerminalAdmin,
            }));

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
        var other = await db.AppUsers.AsNoTracking().SingleAsync(u => u.Email == OtherDispatcherEmail, ct);
        var planner = await db.AppUsers.AsNoTracking().SingleAsync(u => u.Email == PlannerEmail, ct);
        var admin = await db.AppUsers.AsNoTracking().SingleAsync(u => u.Email == TerminalAdminEmail, ct);
        var companyId = dispatcher.CompanyId!.Value;
        var trucks = await db.Trucks.AsNoTracking().Where(t => t.CompanyId == companyId).OrderBy(t => t.TruckId).Select(t => t.TruckId).ToListAsync(ct);
        var drivers = await db.Drivers.AsNoTracking().Where(d => d.CompanyId == companyId).OrderBy(d => d.DriverId).Select(d => d.DriverId).ToListAsync(ct);

        var logins = new Dictionary<string, string>
        {
            [DispatcherEmail] = DemoPassword,
            [OtherDispatcherEmail] = DemoPassword,
            [PlannerEmail] = DemoPassword,
            [TerminalAdminEmail] = DemoPassword,
        };

        return new DemoSeedResult(
            created, terminalId, TerminalCode, companyId, dispatcher.UserId, planner.UserId, trucks, drivers,
            other.CompanyId!.Value, other.UserId, admin.UserId, logins);
    }

    private static TransactionCategory Category(
        int terminalId, string code, string en, string ar, CategoryMode mode, int order, UnitType unit = UnitType.Move) =>
        new() { TerminalId = terminalId, Code = code, NameEn = en, NameAr = ar, Mode = mode, SortOrder = order, UnitType = unit, Flow = TransactionCategory.GuessFlow(code) };

    /// <summary>Roads of the demo yard: a north–south spine from the gate, three cross roads (the south one one-way, east), and side roads.</summary>
    private const string DemoRoads = @"{""type"":""FeatureCollection"",""features"":[{""type"":""Feature"",""properties"":{""name"":""Spine""},""geometry"":{""type"":""LineString"",""coordinates"":[[39.17,21.47],[39.17,21.469],[39.17,21.4655],[39.17,21.4625]]}},{""type"":""Feature"",""properties"":{""name"":""North road""},""geometry"":{""type"":""LineString"",""coordinates"":[[39.164,21.469],[39.1665,21.469],[39.17,21.469],[39.1735,21.469],[39.176,21.469]]}},{""type"":""Feature"",""properties"":{""name"":""Middle road""},""geometry"":{""type"":""LineString"",""coordinates"":[[39.164,21.4655],[39.1665,21.4655],[39.17,21.4655],[39.1735,21.4655],[39.176,21.4655]]}},{""type"":""Feature"",""properties"":{""name"":""South road"",""oneWay"":true},""geometry"":{""type"":""LineString"",""coordinates"":[[39.164,21.4625],[39.17,21.4625],[39.176,21.4625]]}},{""type"":""Feature"",""properties"":{""name"":""West road""},""geometry"":{""type"":""LineString"",""coordinates"":[[39.164,21.469],[39.164,21.4655],[39.164,21.4625]]}},{""type"":""Feature"",""properties"":{""name"":""East road""},""geometry"":{""type"":""LineString"",""coordinates"":[[39.176,21.469],[39.176,21.4655],[39.176,21.4625]]}}]}";
}
