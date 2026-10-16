using Microsoft.EntityFrameworkCore;
using Yardsyncs.Application.Booking;
using Yardsyncs.Domain.Booking;
using Yardsyncs.Domain.Parties;
using Yardsyncs.Infrastructure.Auth;
using Yardsyncs.Infrastructure.Persistence;

namespace Yardsyncs.Infrastructure;

/// <param name="Dispatchers">Sign-in email of each load company's admin (password: <see cref="DemoDataSeeder.DemoPassword"/>).</param>
/// <param name="Drivers">Mobile numbers of drivers with an appointment already on the way (for position reports).</param>
public sealed record LoadSeedResult(int TerminalId, IReadOnlyList<string> Dispatchers, IReadOnlyList<LoadDriver> Drivers);

public sealed record LoadDriver(string Mobile, string AppointmentNo);

/// <summary>
/// Data for the load test (loadtest/): many trucking companies, each with an admin, trucks and drivers, and one
/// appointment per driver already on the way, spread over slots at the end of the booking horizon so they don't take
/// the capacity the booking rush fights over. On top of the demo terminal. Development only.
/// </summary>
public sealed class LoadTestSeeder(YardsyncsDbContext db, DemoDataSeeder demo, PasswordService passwords, TimeProvider clock)
{
    public const string EmailDomain = "load.example";

    public async Task<LoadSeedResult> SeedAsync(int companies, int driversPerCompany, CancellationToken ct = default)
    {
        companies = Math.Clamp(companies, 1, 999);
        driversPerCompany = Math.Clamp(driversPerCompany, 0, 50);
        var terminal = await demo.SeedAsync(ct);
        var now = clock.GetUtcNow();

        var existing = await db.Companies.AsNoTracking().Where(c => c.Name.StartsWith("Load Co ")).CountAsync(ct);
        if (existing == 0)
        {
            // One hash for all: hashing is deliberately slow and every load user has the same password.
            var hash = passwords.Hash(new AppUser(), DemoDataSeeder.DemoPassword);
            for (var c = 1; c <= companies; c++)
            {
                var company = new Company { Name = $"Load Co {c:000}", CrNumber = $"70{c:00000000}", CompanyType = CompanyType.Trucking, CreatedAt = now };
                db.Companies.Add(company);
                await db.SaveChangesAsync(ct);
                db.AppUsers.Add(new AppUser
                {
                    CompanyId = company.CompanyId, Email = $"load{c:000}@{EmailDomain}", FullName = $"Load dispatcher {c:000}",
                    Role = UserRole.CompanyAdmin, PasswordHash = hash, CreatedAt = now,
                });
                for (var d = 1; d <= driversPerCompany; d++)
                {
                    db.Trucks.Add(new Truck { CompanyId = company.CompanyId, PlateNumber = $"{c:000}{d} LDT", PlateNormalized = $"{c:000}{d}LDT" });
                    db.Drivers.Add(new Driver
                    {
                        CompanyId = company.CompanyId, FullName = $"Load driver {c:000}-{d:00}", IdNumber = $"9{c:000}{d:000000}",
                        Mobile = $"+96656{c:000}{d:0000}", PreferredLanguage = "en",
                    });
                }
            }
            await db.SaveChangesAsync(ct);

            // An appointment on the way for every load driver, in the last slots of the horizon (at most 20 per slot).
            var drivers = await db.Drivers.Where(d => d.Mobile.StartsWith("+96656")).OrderBy(d => d.DriverId).ToListAsync(ct);
            var admins = await db.AppUsers.Where(u => u.Email.EndsWith("@" + EmailDomain)).ToDictionaryAsync(u => u.CompanyId!.Value, u => u.UserId, ct);
            var slots = await db.Slots.Where(s => s.TerminalId == terminal.TerminalId && !s.IsClosed && s.StartAt > now)
                .OrderByDescending(s => s.StartAt).Take(200).ToListAsync(ct);
            var timeZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Riyadh");
            var queue = new Queue<Driver>(drivers);
            foreach (var slot in slots)
            {
                var room = Math.Min(20, slot.TotalCapacity - slot.TotalBooked - 2);
                for (var i = 0; i < room && queue.Count > 0; i++)
                {
                    var driver = queue.Dequeue();
                    slot.TotalBooked++;
                    db.Appointments.Add(new Appointment
                    {
                        AppointmentNo = AppointmentNumbers.New(terminal.TerminalCode, DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(slot.StartAt, timeZone).DateTime)),
                        TerminalId = terminal.TerminalId, SlotId = slot.SlotId, CompanyId = driver.CompanyId, DriverId = driver.DriverId,
                        Status = AppointmentStatus.EnRoute, QrToken = AppointmentNumbers.NewQrToken(),
                        CreatedBy = admins[driver.CompanyId], CreatedAt = now,
                    });
                }
                if (queue.Count == 0)
                    break;
            }
            await db.SaveChangesAsync(ct);
        }

        var dispatchers = await db.AppUsers.AsNoTracking().Where(u => u.Email.EndsWith("@" + EmailDomain)).OrderBy(u => u.Email)
            .Select(u => u.Email).ToListAsync(ct);
        var onTheWay = await db.Appointments.AsNoTracking()
            .Where(a => a.Status == AppointmentStatus.EnRoute && a.Driver != null && a.Driver.Mobile.StartsWith("+96656"))
            .OrderBy(a => a.AppointmentId)
            .Select(a => new LoadDriver(a.Driver!.Mobile, a.AppointmentNo))
            .ToListAsync(ct);
        return new LoadSeedResult(terminal.TerminalId, dispatchers, onTheWay);
    }
}
