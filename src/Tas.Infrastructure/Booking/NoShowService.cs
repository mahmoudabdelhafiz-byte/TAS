using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Tas.Domain;
using Tas.Domain.Booking;
using Tas.Infrastructure.Persistence;

namespace Tas.Infrastructure.Booking;

/// <summary>
/// Marks appointments as NO_SHOW once their slot plus the late-grace window has passed without the truck
/// arriving, gives the capacity back and counts the no-show against the company.
/// </summary>
public sealed class NoShowService(TasDbContext db, BookingService booking, TimeProvider clock, ILogger<NoShowService> logger)
{
    public async Task<int> MarkNoShowsAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var marked = 0;

        var terminalIds = await db.Terminals.AsNoTracking().Where(t => t.IsActive).Select(t => t.TerminalId).ToListAsync(ct);
        foreach (var terminalId in terminalIds)
        {
            var settings = TerminalSettings.From(
                await db.TerminalSettings.AsNoTracking().Where(s => s.TerminalId == terminalId).ToListAsync(ct));
            var cutoff = now.AddMinutes(-settings.LateGraceMinutes);

            // Arrived trucks are at the terminal, just not through the gate yet — never a no-show.
            var overdue = await db.Appointments.AsNoTracking()
                .Include(a => a.Items)
                .Where(a => a.TerminalId == terminalId
                            && (a.Status == AppointmentStatus.Confirmed || a.Status == AppointmentStatus.EnRoute)
                            && a.Slot.EndAt < cutoff)
                .ToListAsync(ct);

            foreach (var appointment in overdue)
            {
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                if (!await booking.TrySetStatusAsync(appointment, AppointmentStatus.NoShow, ct))
                {
                    await tx.RollbackAsync(ct);
                    continue;
                }

                await booking.ReleaseCapacityAsync(appointment, ct);
                await db.Companies
                    .Where(c => c.CompanyId == appointment.CompanyId)
                    .ExecuteUpdateAsync(s => s.SetProperty(c => c.NoShowCount, c => c.NoShowCount + 1), ct);
                booking.AddHistory(appointment, AppointmentStatus.NoShow, null, "No-show: not arrived by the end of the late-grace window", now);
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                marked++;
            }
        }

        if (marked > 0)
            logger.LogInformation("Marked {Count} appointment(s) as no-show", marked);
        return marked;
    }
}

/// <summary>Runs the no-show check periodically. Config: NoShow:Enabled (default true), NoShow:IntervalMinutes (default 10).</summary>
public sealed class NoShowWorker(IServiceScopeFactory scopes, IConfiguration config, ILogger<NoShowWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.GetValue("NoShow:Enabled", true))
            return;

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(config.GetValue("NoShow:IntervalMinutes", 10.0)));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<NoShowService>().MarkNoShowsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "No-show run failed");
            }
        }
    }
}
