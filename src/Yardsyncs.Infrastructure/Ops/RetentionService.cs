using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Yardsyncs.Infrastructure.Persistence;

namespace Yardsyncs.Infrastructure.Ops;

/// <summary>
/// Configuration section "Privacy": how long personal data is kept (Saudi PDPL: keep personal data no longer than
/// the purpose needs). Visits, appointments and the audit trail stay; what is removed is the personal detail in them.
/// </summary>
public sealed class PrivacyOptions
{
    public bool CleanUpEnabled { get; set; } = true;
    public double CleanUpIntervalHours { get; set; } = 24;

    /// <summary>Phone positions (last position, arrival estimates, area entries): the times stay, the coordinates go.</summary>
    public int LocationDays { get; set; } = 30;

    /// <summary>Who a message went to (phone / email) and its values; the message record stays for the counts.</summary>
    public int MessageDays { get; set; } = 180;

    /// <summary>Events delivered to the terminal's systems (they carry positions and plates).</summary>
    public int EventDays { get; set; } = 90;

    /// <summary>Used or expired sign-in sessions and password links (they carry IP addresses); sign-in codes go after a day.</summary>
    public int TokenDays { get; set; } = 30;
}

/// <param name="At">When the clean-up ran.</param>
/// <param name="Removed">What was removed or cleared, by kind, e.g. "positions" → 120.</param>
public sealed record RetentionRun(DateTimeOffset At, IReadOnlyDictionary<string, int> Removed);

/// <summary>Removes personal data that is older than <see cref="PrivacyOptions"/> allows.</summary>
public sealed class RetentionService(YardsyncsDbContext db, IOptions<PrivacyOptions> options, TimeProvider clock, ILogger<RetentionService> logger)
{
    /// <summary>The latest run in this process, for /api/ops/status.</summary>
    public static RetentionRun? LastRun { get; private set; }

    public async Task<RetentionRun> RunAsync(CancellationToken ct = default)
    {
        var o = options.Value;
        var now = clock.GetUtcNow();
        var location = now.AddDays(-Math.Max(1, o.LocationDays));
        var messages = now.AddDays(-Math.Max(1, o.MessageDays));
        var events = now.AddDays(-Math.Max(1, o.EventDays));
        var tokens = now.AddDays(-Math.Max(1, o.TokenDays));
        var codes = now.AddDays(-1);
        var removed = new Dictionary<string, int>();

        removed["positions"] =
            await db.Database.ExecuteSqlAsync($"""
                UPDATE appointment SET last_latitude = NULL, last_longitude = NULL
                WHERE last_latitude IS NOT NULL AND (last_position_at IS NULL OR last_position_at < {location})
                """, ct)
            + await db.Database.ExecuteSqlAsync($"""
                UPDATE appointment_eta SET latitude = NULL, longitude = NULL
                WHERE latitude IS NOT NULL AND recorded_at < {location}
                """, ct)
            + await db.Database.ExecuteSqlAsync($"""
                UPDATE geofence_event SET latitude = NULL, longitude = NULL
                WHERE latitude IS NOT NULL AND occurred_at < {location}
                """, ct);

        removed["messageRecipients"] = await db.Database.ExecuteSqlAsync($"""
            UPDATE notification_log SET recipient = NULL, params = NULL
            WHERE (recipient IS NOT NULL OR params IS NOT NULL) AND status <> 'QUEUED' AND created_at < {messages}
            """, ct);

        removed["integrationEvents"] = await db.Database.ExecuteSqlAsync($"""
            DELETE FROM integration_event WHERE status <> 'QUEUED' AND created_at < {events}
            """, ct);

        removed["signInCodes"] = await db.Database.ExecuteSqlAsync($"DELETE FROM driver_otp WHERE created_at < {codes}", ct);
        removed["sessions"] = await db.Database.ExecuteSqlAsync($"""
            DELETE FROM refresh_token WHERE expires_at < {tokens} OR revoked_at < {tokens}
            """, ct);
        removed["passwordLinks"] = await db.Database.ExecuteSqlAsync($"""
            DELETE FROM password_token WHERE expires_at < {tokens} OR used_at < {tokens}
            """, ct);
        // A phone the driver no longer uses doesn't need its push address.
        removed["pushTokens"] = await db.Database.ExecuteSqlAsync($"""
            UPDATE driver_device SET push_token = NULL WHERE NOT is_active AND push_token IS NOT NULL
            """, ct);

        var run = new RetentionRun(now, removed);
        LastRun = run;
        if (removed.Values.Any(n => n > 0))
            logger.LogInformation("Personal data clean-up: {Removed}", string.Join(", ", removed.Where(r => r.Value > 0).Select(r => $"{r.Key} {r.Value}")));
        return run;
    }
}

/// <summary>Runs the clean-up a minute after start and then every Privacy:CleanUpIntervalHours (default daily).</summary>
public sealed class RetentionWorker(IServiceScopeFactory scopes, IOptions<PrivacyOptions> options, ILogger<RetentionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.CleanUpEnabled)
            return;
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromHours(Math.Max(1, options.Value.CleanUpIntervalHours)));
            do
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<RetentionService>().RunAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Personal data clean-up failed");
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
