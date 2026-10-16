using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Yardsyncs.Application.Tos;
using Yardsyncs.Domain;
using Yardsyncs.Infrastructure.Persistence;

namespace Yardsyncs.Infrastructure.Ops;

public sealed record OutboxStatus(int Queued, int FailedLast24h, int SentLast24h, double? OldestQueuedMinutes);

/// <summary>What an administrator or a monitoring system needs to see at a glance.</summary>
public sealed record OpsStatus(
    string Version, string Environment, DateTimeOffset StartedAt, DateTimeOffset Now, string Tos,
    DatabaseStatus Database, OutboxStatus Notifications, OutboxStatus IntegrationEvents, int FollowedContainers, int LiveVisits,
    IReadOnlyList<string> Warnings, RetentionRun? LastCleanUp, AssistantToday Assistant, IReadOnlyList<TerminalSetup> Terminals,
    IReadOnlyList<string>? LanguagePacks = null);

/// <summary>How one terminal is set up (no secrets): its TOS, message providers and sender names, and its webhook.</summary>
public sealed record TerminalSetup(
    string Code, string Name, string Tos, string Brand, string SmsProvider, string? SmsSender, string EmailProvider, string? EmailFrom,
    string PushProvider, bool OwnMessageSettings, bool Webhook, string Country = "SA", string DriverChannel = "Sms");

/// <summary>The AI assistant today (UTC day): questions asked and tokens used, for cost.</summary>
public sealed record AssistantToday(bool Enabled, bool LiveData, int Questions, long InputTokens, long OutputTokens);

/// <summary>Version, database and background work (message and event outboxes) for the ops screen and monitoring.</summary>
public sealed class OpsStatusService(
    YardsyncsDbContext db, DatabaseMigrator migrator, ITosAdapter tos, TimeProvider clock,
    Microsoft.Extensions.Configuration.IConfiguration config, Microsoft.Extensions.Hosting.IHostEnvironment env,
    Microsoft.Extensions.Options.IOptions<Assistant.AssistantOptions> assistant,
    TerminalProfiles profiles,
    Microsoft.Extensions.Options.IOptionsMonitor<Integration.IntegrationOptions> integration,
    Brands.BrandingService? branding = null)
{
    private sealed class UsageRow
    {
        public int Questions { get; set; }
        public long InputTokens { get; set; }
        public long OutputTokens { get; set; }
    }

    private static readonly DateTimeOffset Started = DateTimeOffset.UtcNow;

    public static string Version { get; } =
        typeof(OpsStatusService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? typeof(OpsStatusService).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    /// <param name="sees">The terminals whose set-up to list (an admin sees their own terminals); null: all.</param>
    public async Task<OpsStatus> StatusAsync(string environment, CancellationToken ct = default, Func<int, bool>? sees = null)
    {
        var now = clock.GetUtcNow();
        var since = now.AddHours(-24);

        var notifications = db.NotificationLogs.AsNoTracking();
        var n = new
        {
            Queued = await notifications.CountAsync(x => x.Status == NotificationStatus.Queued, ct),
            Failed = await notifications.CountAsync(x => x.Status == NotificationStatus.Failed && x.CreatedAt >= since, ct),
            Sent = await notifications.CountAsync(x => x.Status == NotificationStatus.Sent && x.SentAt >= since, ct),
            Oldest = await notifications.Where(x => x.Status == NotificationStatus.Queued).MinAsync(x => (DateTimeOffset?)x.CreatedAt, ct),
        };
        var events = db.IntegrationEvents.AsNoTracking();
        var e = new
        {
            Queued = await events.CountAsync(x => x.Status == IntegrationEventStatus.Queued, ct),
            Failed = await events.CountAsync(x => x.Status == IntegrationEventStatus.Failed && x.CreatedAt >= since, ct),
            Sent = await events.CountAsync(x => x.Status == IntegrationEventStatus.Sent && x.SentAt >= since, ct),
            Oldest = await events.Where(x => x.Status == IntegrationEventStatus.Queued).MinAsync(x => (DateTimeOffset?)x.CreatedAt, ct),
        };

        static double? Age(DateTimeOffset? oldest, DateTimeOffset now) => oldest is null ? null : Math.Round((now - oldest.Value).TotalMinutes, 1);

        var terminals = (await db.Terminals.AsNoTracking().OrderBy(t => t.Code).Select(t => new { t.TerminalId, t.Code, t.Name }).ToListAsync(ct))
            .Where(t => sees is null || sees(t.TerminalId)).ToList();
        var brands = new Dictionary<int, string>();
        foreach (var t in terminals)
            brands[t.TerminalId] = branding is null ? profiles.BrandFor(t.Code) : await branding.MessageNameAsync(t.TerminalId, ct);
        var setups = terminals.Select(t =>
        {
            var m = profiles.NotificationsFor(t.Code);
            var smsSender = m.SmsProvider.Equals("Twilio", StringComparison.OrdinalIgnoreCase) ? m.Twilio.SmsFrom : m.Unifonic.SenderId;
            return new TerminalSetup(t.Code, t.Name, profiles.TosFor(t.Code), brands[t.TerminalId], m.SmsProvider,
                string.IsNullOrWhiteSpace(smsSender) ? null : smsSender, m.EmailProvider, string.IsNullOrWhiteSpace(m.Smtp.From) ? null : m.Smtp.From,
                m.PushProvider, profiles.HasOwnNotifications(t.Code),
                integration.CurrentValue.Terminals.TryGetValue(t.Code, out var hook) && hook.Url.Length > 0,
                profiles.CountryFor(t.Code).Code, m.DriverChannel.ToString());
        }).ToList();

        return new OpsStatus(
            Version, environment, Started, now,
            tos is Tos.TosRouter router ? router.Summary(terminals.Select(t => t.Code)) : tos.Source,
            await migrator.StatusAsync(ct),
            new OutboxStatus(n.Queued, n.Failed, n.Sent, Age(n.Oldest, now)),
            new OutboxStatus(e.Queued, e.Failed, e.Sent, Age(e.Oldest, now)),
            await db.ContainerWatches.CountAsync(w => w.EndedAt == null, ct),
            await db.Appointments.CountAsync(a => a.Status == Yardsyncs.Domain.Booking.AppointmentStatus.EnRoute
                                                  || a.Status == Yardsyncs.Domain.Booking.AppointmentStatus.Arrived
                                                  || a.Status == Yardsyncs.Domain.Booking.AppointmentStatus.GateIn, ct),
            SecurityChecks.Warnings(config, env),
            RetentionService.LastRun,
            await AssistantTodayAsync(now, ct),
            setups,
            Yardsyncs.Application.Languages.LanguageCatalog.Packs.Select(p => $"{p.Code} {p.Name}: " + string.Join(", ",
                new[] { (p.Portal.Count, "portal"), (p.App.Count, "app"), (p.Messages.Count, "messages") }
                    .Where(x => x.Item1 > 0).Select(x => $"{x.Item2} {x.Item1}"))).ToList());
    }

    private async Task<AssistantToday> AssistantTodayAsync(DateTimeOffset now, CancellationToken ct)
    {
        var day = DateOnly.FromDateTime(now.UtcDateTime);
        var usage = await db.Database.SqlQuery<UsageRow>($"""
            SELECT coalesce(sum(questions), 0)::int AS "Questions", coalesce(sum(input_tokens), 0)::bigint AS "InputTokens",
                   coalesce(sum(output_tokens), 0)::bigint AS "OutputTokens"
            FROM assistant_usage WHERE day = {day}
            """).SingleAsync(ct);
        return new AssistantToday(assistant.Value.Enabled, assistant.Value.LiveData, usage.Questions, usage.InputTokens, usage.OutputTokens);
    }
}

/// <summary>Ready = the database answers and has every migration this build needs.</summary>
public sealed class DatabaseHealthCheck(DatabaseMigrator migrator) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            var status = await migrator.StatusAsync(ct);
            return status.UpToDate
                ? HealthCheckResult.Healthy($"Database at version {status.Current}.")
                : HealthCheckResult.Unhealthy($"Database at version {status.Current}; waiting for: {string.Join(", ", status.Pending)}.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("The database cannot be reached.", ex);
        }
    }
}
