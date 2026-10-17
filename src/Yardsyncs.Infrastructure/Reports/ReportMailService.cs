using System.Net.Mail;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Yardsyncs.Application.Auth;
using Yardsyncs.Application.Capacity;
using Yardsyncs.Application.Notifications;
using Yardsyncs.Application.Reports;
using Yardsyncs.Domain;
using Yardsyncs.Infrastructure.Notifications;
using Yardsyncs.Infrastructure.Persistence;

namespace Yardsyncs.Infrastructure.Reports;

public sealed record ReportSubscriptionView(int SubscriptionId, string Email, string Language, bool Daily, bool Weekly, DateOnly? LastDailyOn, DateOnly? LastWeeklyOn);

public sealed record ReportSubscriptionInput(string? Email, string? Language, bool Daily = true, bool Weekly = false);

/// <summary>
/// Reports by email: who gets them (per terminal), and sending the daily one (yesterday and today's bookings) and the
/// weekly one (the seven days before) at the terminal's report hour, each once, with the figures as Excel.
/// </summary>
public sealed class ReportMailService(
    YardsyncsDbContext db, KpiService kpis, CompanyReportService companies, ReportExportService exports, NotificationSenders senders,
    TimeProvider clock, ILogger<ReportMailService> logger)
{
    /// <summary>The most people one terminal sends reports to.</summary>
    public const int MaxSubscriptions = 50;

    public async Task<IReadOnlyList<ReportSubscriptionView>> ListAsync(int terminalId, CancellationToken ct = default) =>
        (await db.ReportSubscriptions.AsNoTracking().Where(s => s.TerminalId == terminalId).OrderBy(s => s.Email).ToListAsync(ct))
        .Select(View).ToList();

    /// <summary>Adds someone, or changes what an address already gets.</summary>
    public async Task<AdminResult<ReportSubscriptionView>> SaveAsync(CurrentUser actor, int terminalId, ReportSubscriptionInput input, CancellationToken ct = default)
    {
        var email = input.Email?.Trim().ToLowerInvariant() ?? "";
        if (email.Length is 0 or > 200 || !MailAddress.TryCreate(email, out _))
            return AdminResult<ReportSubscriptionView>.Invalid("Give a valid email address.");
        var language = (input.Language ?? "en").Trim().ToLowerInvariant();
        if (language is not ("en" or "ar"))
            return AdminResult<ReportSubscriptionView>.Invalid("Reports are written in English (en) or Arabic (ar).");
        if (!input.Daily && !input.Weekly)
            return AdminResult<ReportSubscriptionView>.Invalid("Choose the daily report, the weekly one or both.");
        if (!await db.Terminals.AnyAsync(t => t.TerminalId == terminalId, ct))
            return AdminResult<ReportSubscriptionView>.NotFound($"Terminal {terminalId} not found.");

        var row = await db.ReportSubscriptions.SingleOrDefaultAsync(s => s.TerminalId == terminalId && s.Email == email, ct);
        var created = row is null;
        if (row is null)
        {
            if (await db.ReportSubscriptions.CountAsync(s => s.TerminalId == terminalId, ct) >= MaxSubscriptions)
                return AdminResult<ReportSubscriptionView>.Conflict($"A terminal sends reports to at most {MaxSubscriptions} addresses.");
            row = new ReportSubscription { TerminalId = terminalId, Email = email, CreatedAt = clock.GetUtcNow(), CreatedBy = actor.UserId };
            db.ReportSubscriptions.Add(row);
        }
        row.Language = language;
        row.Daily = input.Daily;
        row.Weekly = input.Weekly;
        await db.SaveChangesAsync(ct);
        return created ? AdminResult<ReportSubscriptionView>.Created(View(row)) : AdminResult<ReportSubscriptionView>.Ok(View(row));
    }

    public async Task<bool> RemoveAsync(int terminalId, int subscriptionId, CancellationToken ct = default) =>
        await db.ReportSubscriptions.Where(s => s.TerminalId == terminalId && s.SubscriptionId == subscriptionId).ExecuteDeleteAsync(ct) > 0;

    /// <summary>Sends the reports that are due now; returns how many emails went out.</summary>
    public async Task<int> SendDueAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var terminalIds = await db.ReportSubscriptions.AsNoTracking().Select(s => s.TerminalId).Distinct().ToListAsync(ct);
        var sent = 0;
        foreach (var terminalId in terminalIds)
            sent += await SendDueAsync(terminalId, now, ct);
        return sent;
    }

    private async Task<int> SendDueAsync(int terminalId, DateTimeOffset now, CancellationToken ct)
    {
        var terminal = await db.Terminals.AsNoTracking().Where(t => t.TerminalId == terminalId).Select(t => new { t.Name, t.TimeZone }).SingleAsync(ct);
        var settings = TerminalSettings.From(await db.TerminalSettings.AsNoTracking().Where(s => s.TerminalId == terminalId).ToListAsync(ct));
        var local = TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById(terminal.TimeZone));
        if (local.Hour < settings.ReportHour)
            return 0;
        var today = DateOnly.FromDateTime(local.DateTime);
        var weeklyToday = (int)local.DayOfWeek == settings.ReportWeekday;

        var due = await db.ReportSubscriptions
            .Where(s => s.TerminalId == terminalId
                        && ((s.Daily && (s.LastDailyOn == null || s.LastDailyOn < today))
                            || (weeklyToday && s.Weekly && (s.LastWeeklyOn == null || s.LastWeeklyOn < today))))
            .ToListAsync(ct);
        if (due.Count == 0)
            return 0;

        var email = senders.For(NotificationChannel.Email, await TerminalProfiles.CodeAsync(db, terminalId, ct));
        if (email is null)
        {
            logger.LogWarning("Reports for terminal {TerminalId} not sent: no email sender", terminalId);
            return 0;
        }

        var yesterday = today.AddDays(-1);
        KpiReport? daily = null, todayReport = null, week = null;
        IReadOnlyList<ReportRow>? weekCompanies = null;
        var files = new Dictionary<(string Kind, string Language), byte[]>();
        var sent = 0;

        foreach (var s in due)
        {
            var language = s.Language.Trim();
            if (s.Daily && (s.LastDailyOn is null || s.LastDailyOn < today))
            {
                daily ??= await kpis.ReportAsync(terminalId, yesterday, yesterday, ct);
                todayReport ??= await kpis.ReportAsync(terminalId, today, today, ct);
                var (subject, body) = ReportDigest.Daily(language, terminal.Name, daily, todayReport);
                if (!files.TryGetValue(("daily", language), out var file))
                    files[("daily", language)] = file = await exports.KpisAsync(terminalId, yesterday, yesterday, language, ct);
                if (await SendAsync(email, s.Email, subject, body, file, $"report-{yesterday:yyyy-MM-dd}.xlsx", ct))
                    sent++;
                s.LastDailyOn = today; // once a day, also when the mail server refused it (logged)
            }
            if (weeklyToday && s.Weekly && (s.LastWeeklyOn is null || s.LastWeeklyOn < today))
            {
                var from = today.AddDays(-7);
                week ??= await kpis.ReportAsync(terminalId, from, yesterday, ct);
                weekCompanies ??= await companies.CompaniesAsync(terminalId, from, yesterday, ct);
                var (subject, body) = ReportDigest.Weekly(language, terminal.Name, week, weekCompanies);
                if (!files.TryGetValue(("weekly", language), out var file))
                    files[("weekly", language)] = file = await exports.KpisAsync(terminalId, from, yesterday, language, ct);
                if (await SendAsync(email, s.Email, subject, body, file, $"report-{from:yyyy-MM-dd}-{yesterday:yyyy-MM-dd}.xlsx", ct))
                    sent++;
                s.LastWeeklyOn = today;
            }
        }
        await db.SaveChangesAsync(ct);
        return sent;
    }

    private async Task<bool> SendAsync(INotificationSender sender, string to, string subject, string body, byte[] file, string fileName, CancellationToken ct)
    {
        try
        {
            await sender.SendAsync(to, new RenderedMessage(subject, body)
            {
                Attachments = [new MessageAttachment(fileName, ReportExportService.ContentType, file)],
            }, ct);
            return true;
        }
        catch (NotificationSendException ex)
        {
            logger.LogWarning(ex, "Report to {Recipient} not sent", Ops.SecurityChecks.Mask(to));
            return false;
        }
    }

    private static ReportSubscriptionView View(ReportSubscription s) =>
        new(s.SubscriptionId, s.Email, s.Language.Trim(), s.Daily, s.Weekly, s.LastDailyOn, s.LastWeeklyOn);
}

/// <summary>Sends the emailed reports when due. Config: Reports:Enabled (default true), Reports:IntervalMinutes (default 5).</summary>
public sealed class ReportMailWorker(IServiceScopeFactory scopes, IConfiguration config, ILogger<ReportMailWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.GetValue("Reports:Enabled", true))
            return;

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(config.GetValue("Reports:IntervalMinutes", 5.0)));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                var sent = await scope.ServiceProvider.GetRequiredService<ReportMailService>().SendDueAsync(stoppingToken);
                if (sent > 0)
                    logger.LogInformation("{Count} report email(s) sent", sent);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Report run failed");
            }
        }
    }
}
