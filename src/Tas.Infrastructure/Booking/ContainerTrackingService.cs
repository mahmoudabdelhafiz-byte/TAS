using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tas.Application.Auth;
using Tas.Application.Booking;
using Tas.Application.Capacity;
using Tas.Application.Notifications;
using Tas.Application.Tos;
using Tas.Domain;
using Tas.Domain.Booking;
using Tas.Domain.Parties;
using Tas.Infrastructure.Persistence;

namespace Tas.Infrastructure.Booking;

public sealed record TrackInput(string? BlNo = null, IReadOnlyList<string>? Containers = null);

/// <summary>Where a container stands for the trucking company.</summary>
public static class ContainerState
{
    /// <summary>In the yard, released, no holds: can be collected (book a truck).</summary>
    public const string Ready = "READY";

    /// <summary>Not discharged / not in the yard yet.</summary>
    public const string NotAvailable = "NOT_AVAILABLE";

    /// <summary>A hold or missing release: customs, shipping line, terminal, unpaid charges.</summary>
    public const string OnHold = "ON_HOLD";

    /// <summary>On a live appointment.</summary>
    public const string Booked = "BOOKED";

    /// <summary>Picked up through Yardsyncs and gone from the yard.</summary>
    public const string Collected = "COLLECTED";

    /// <summary>Not a valid container number.</summary>
    public const string Invalid = "INVALID";
}

/// <param name="Type">Availability, CustomsRelease, LineRelease, TerminalHold, Payment…</param>
/// <param name="Result">Pass, Warn or Fail.</param>
public sealed record TrackCheck(string Type, string Result, string? Message);

/// <param name="Appointment">The live (or last completed) appointment, when the user may see it.</param>
public sealed record TrackedAppointment(string AppointmentNo, AppointmentStatus Status, DateTimeOffset SlotStart, DateTimeOffset SlotEnd, DateTimeOffset? GateOutAt);

public sealed record TrackedContainer(
    string ContainerNo, string? IsoType, int? SizeFt, string State, IReadOnlyList<TrackCheck> Checks,
    IReadOnlyList<string> Problems, IReadOnlyList<string> Warnings, TrackedAppointment? Appointment, bool BookedByOthers, bool Following);

public sealed record TrackResult(string? BlNo, DateTimeOffset CheckedAt, IReadOnlyList<TrackedContainer> Containers);

public sealed record WatchView(string ContainerNo, string? State, string? Message, DateTimeOffset? LastCheckedAt, DateTimeOffset? ReadyNotifiedAt, DateTimeOffset CreatedAt);

/// <summary>Configuration section "Tracking": how often followed containers are re-checked.</summary>
public sealed class TrackingOptions
{
    public bool Enabled { get; set; } = true;
    public int IntervalMinutes { get; set; } = 15;

    /// <summary>A container is followed at most this long.</summary>
    public int FollowDays { get; set; } = 30;

    public int MaxFollowedPerUser { get; set; } = 200;
}

/// <summary>
/// "Track containers": the status of import containers for trucking companies — in the yard, customs and line
/// release, holds, charges, booked or collected — by bill of lading or container numbers, without booking; and
/// following containers, with an email the moment one becomes ready to collect.
/// </summary>
public sealed class ContainerTrackingService(
    TasDbContext db, ITosAdapter tos, IOptions<TrackingOptions> options, TimeProvider clock, ILogger<ContainerTrackingService> logger)
{
    public async Task<AdminResult<TrackResult>> TrackAsync(CurrentUser user, int terminalId, TrackInput input, CancellationToken ct = default)
    {
        var blNo = string.IsNullOrWhiteSpace(input.BlNo) ? null : input.BlNo.Trim().ToUpperInvariant();
        List<(string ContainerNo, string? Iso)> containers;
        if (blNo is not null)
        {
            IReadOnlyList<TosBlUnit>? units;
            try
            {
                units = await tos.FindBlContainersAsync(terminalId, blNo, ct);
            }
            catch (TosUnavailableException)
            {
                return AdminResult<TrackResult>.Conflict(ContainerLookupService.TosDown);
            }
            if (units is null)
                return AdminResult<TrackResult>.NotFound($"Bill of lading {blNo} was not found at this terminal.");
            containers = units.Select(u => (ContainerNumber.Normalize(u.ContainerNo), u.IsoType)).DistinctBy(c => c.Item1).ToList();
        }
        else
        {
            containers = IsoSizeType.SplitContainers(input.Containers ?? Array.Empty<string>()).Select(c => (c, (string?)null)).ToList();
            if (containers.Count == 0)
                return AdminResult<TrackResult>.Invalid("Enter a bill of lading or at least one container number.");
        }
        if (containers.Count > ContainerLookupService.MaxContainers)
            return AdminResult<TrackResult>.Invalid($"{containers.Count} containers is more than {ContainerLookupService.MaxContainers} at a time.");

        List<TrackedContainer> result;
        try
        {
            result = await StatesAsync(terminalId, containers, user, ct);
        }
        catch (TosUnavailableException)
        {
            return AdminResult<TrackResult>.Conflict(ContainerLookupService.TosDown);
        }
        catch (InvalidOperationException ex)
        {
            return AdminResult<TrackResult>.Invalid(ex.Message);
        }

        var numbers = result.Select(r => r.ContainerNo).ToList();
        var following = await db.ContainerWatches.AsNoTracking()
            .Where(w => w.TerminalId == terminalId && w.UserId == user.UserId && w.EndedAt == null && numbers.Contains(w.ContainerNo))
            .Select(w => w.ContainerNo).ToListAsync(ct);
        return AdminResult<TrackResult>.Ok(new TrackResult(blNo, clock.GetUtcNow(),
            result.Select(r => r with { Following = following.Contains(r.ContainerNo) }).ToList()));
    }

    /// <summary>The terminal's import pickup move type: the checks a container must pass to be collected.</summary>
    private async Task<TransactionCategory> PickupCategoryAsync(int terminalId, CancellationToken ct)
    {
        var categories = await db.TransactionCategories.AsNoTracking()
            .Where(c => c.TerminalId == terminalId && c.IsActive && c.Mode == CategoryMode.Container)
            .OrderBy(c => c.SortOrder)
            .ToListAsync(ct);
        return categories.FirstOrDefault(c => c.Code.StartsWith("IMP", StringComparison.OrdinalIgnoreCase))
               ?? categories.FirstOrDefault(c => c.Code.EndsWith("_PICKUP", StringComparison.OrdinalIgnoreCase) && !c.Code.StartsWith("MTY", StringComparison.OrdinalIgnoreCase))
               ?? throw new InvalidOperationException("This terminal has no import pickup move type to check containers against.");
    }

    /// <summary>Checks the containers against the TOS and Yardsyncs' own appointments.</summary>
    /// <param name="viewer">Null for the background check (no appointment details needed).</param>
    private async Task<List<TrackedContainer>> StatesAsync(int terminalId, IReadOnlyList<(string ContainerNo, string? Iso)> containers, CurrentUser? viewer, CancellationToken ct)
    {
        var category = await PickupCategoryAsync(terminalId, ct);
        var wellFormed = containers.Select((c, i) => (c.ContainerNo, Index: i)).Where(c => ContainerNumber.HasValidFormat(c.ContainerNo)).ToList();
        var checks = wellFormed.Count == 0
            ? new List<ItemCheck>()
            : await ItemChecks.RunAsync(tos, terminalId,
                wellFormed.Select(c => new ItemToCheck(c.Index, category.Code, category.Mode, c.ContainerNo, null, null, null)).ToList(),
                clock.GetUtcNow(), ct);

        var numbers = containers.Select(c => c.ContainerNo).ToList();
        var visits = await db.Appointments.AsNoTracking()
            .Where(a => a.TerminalId == terminalId && a.Status != AppointmentStatus.Cancelled && a.Status != AppointmentStatus.Draft
                        && a.Status != AppointmentStatus.NoShow && a.Status != AppointmentStatus.RejectedAtGate)
            .SelectMany(a => a.Items.Where(i => i.ContainerNo != null && numbers.Contains(i.ContainerNo)),
                (a, i) => new { i.ContainerNo, a.AppointmentNo, a.CompanyId, a.Status, a.Slot.StartAt, a.Slot.EndAt, a.GateOutAt, i.CategoryId })
            .ToListAsync(ct);

        return containers.Select((c, index) =>
        {
            var size = IsoSizeType.LengthFeet(c.Iso);
            if (!ContainerNumber.HasValidFormat(c.ContainerNo))
                return new TrackedContainer(c.ContainerNo, c.Iso, size, ContainerState.Invalid, [],
                    [$"'{c.ContainerNo}' is not a container number (4 letters + 7 digits)"], [], null, false, false);

            var mine = checks.Where(k => k.Index == index).ToList();
            var problems = mine.Where(k => k.Result == CheckResult.Fail).Select(k => k.Message ?? k.Type.ToString()).ToList();
            var warnings = mine.Where(k => k.Result == CheckResult.Warn).Select(k => k.Message ?? k.Type.ToString()).ToList();
            var trackChecks = mine.Where(k => k.Type != CheckType.ContainerNumber || k.Result != CheckResult.Pass)
                .Select(k => new TrackCheck(k.Type.ToString(), k.Result.ToString(), k.Message)).ToList();

            var mineVisits = visits.Where(v => v.ContainerNo == c.ContainerNo).ToList();
            var live = mineVisits.FirstOrDefault(v => Appointment.IsLive(v.Status));
            var collected = mineVisits.Where(v => v.Status == AppointmentStatus.Completed && v.CategoryId == category.CategoryId)
                .OrderByDescending(v => v.GateOutAt).FirstOrDefault();
            var notInYard = mine.Any(k => k.Type == CheckType.Availability && k.Result == CheckResult.Fail);
            var badNumber = mine.Any(k => k.Type == CheckType.ContainerNumber && k.Result == CheckResult.Fail);

            var state = badNumber ? ContainerState.Invalid
                : live is not null ? ContainerState.Booked
                : collected is not null && notInYard ? ContainerState.Collected
                : notInYard ? ContainerState.NotAvailable
                : problems.Count > 0 ? ContainerState.OnHold
                : ContainerState.Ready;

            // Company users see only their own company's appointments; someone else's booking is just "booked".
            var shown = live ?? (state == ContainerState.Collected ? collected : null);
            var visible = shown is not null && (viewer is null || !viewer.IsCompanyUser || shown.CompanyId == viewer.CompanyId);
            var appointment = visible ? new TrackedAppointment(shown!.AppointmentNo, shown.Status, shown.StartAt, shown.EndAt, shown.GateOutAt) : null;
            var byOthers = shown is not null && !visible;
            return new TrackedContainer(c.ContainerNo, c.Iso, size, state, trackChecks, problems, warnings, appointment, byOthers, false);
        }).ToList();
    }

    // ================================================================ following

    public async Task<IReadOnlyList<WatchView>> FollowedAsync(CurrentUser user, int terminalId, CancellationToken ct = default) =>
        await db.ContainerWatches.AsNoTracking()
            .Where(w => w.TerminalId == terminalId && w.UserId == user.UserId && w.EndedAt == null)
            .OrderBy(w => w.ContainerNo)
            .Select(w => new WatchView(w.ContainerNo, w.LastState, w.LastMessage, w.LastCheckedAt, w.ReadyNotifiedAt, w.CreatedAt))
            .ToListAsync(ct);

    /// <summary>Follows the containers (again, if followed before); their state is checked straight away.</summary>
    public async Task<AdminResult<IReadOnlyList<WatchView>>> FollowAsync(CurrentUser user, int terminalId, IReadOnlyList<string>? containers, CancellationToken ct = default)
    {
        var numbers = IsoSizeType.SplitContainers(containers ?? Array.Empty<string>()).ToList();
        if (numbers.Count == 0)
            return AdminResult<IReadOnlyList<WatchView>>.Invalid("Enter at least one container number.");
        var wrong = numbers.Where(n => !ContainerNumber.HasValidFormat(n) || !ContainerNumber.IsValid(n)).ToList();
        if (wrong.Count > 0)
            return AdminResult<IReadOnlyList<WatchView>>.Invalid($"Not valid container numbers: {string.Join(", ", wrong)}.");

        var now = clock.GetUtcNow();
        var rows = await db.ContainerWatches.Where(w => w.TerminalId == terminalId && w.UserId == user.UserId).ToListAsync(ct);
        var active = rows.Count(w => w.EndedAt == null);
        foreach (var number in numbers)
        {
            var row = rows.FirstOrDefault(w => w.ContainerNo == number);
            if (row is { EndedAt: null })
                continue;
            if (++active > options.Value.MaxFollowedPerUser)
                return AdminResult<IReadOnlyList<WatchView>>.Invalid($"You can follow at most {options.Value.MaxFollowedPerUser} containers.");
            if (row is null)
            {
                row = new ContainerWatch { TerminalId = terminalId, UserId = user.UserId, ContainerNo = number };
                db.ContainerWatches.Add(row);
            }
            row.CreatedAt = now;
            row.EndedAt = null;
            row.LastState = null;
            row.LastMessage = null;
            row.LastCheckedAt = null;
            row.ReadyNotifiedAt = null;
        }
        await db.SaveChangesAsync(ct);

        try
        {
            await CheckAsync(terminalId, w => w.UserId == user.UserId && numbers.Contains(w.ContainerNo), notify: false, ct);
        }
        catch (TosUnavailableException)
        {
            // Checked by the next round.
        }
        return AdminResult<IReadOnlyList<WatchView>>.Ok(await FollowedAsync(user, terminalId, ct));
    }

    public Task<int> UnfollowAsync(CurrentUser user, int terminalId, string containerNo, CancellationToken ct = default)
    {
        var number = ContainerNumber.Normalize(containerNo);
        var now = clock.GetUtcNow();
        return db.ContainerWatches.Where(w => w.TerminalId == terminalId && w.UserId == user.UserId && w.ContainerNo == number && w.EndedAt == null)
            .ExecuteUpdateAsync(x => x.SetProperty(w => w.EndedAt, now), ct);
    }

    /// <summary>
    /// The background round: re-checks followed containers not checked for <see cref="TrackingOptions.IntervalMinutes"/>;
    /// when one becomes ready to collect, its follower gets an email. Returns the number of emails queued.
    /// </summary>
    public async Task<int> CheckFollowedAsync(CancellationToken ct = default)
    {
        var due = clock.GetUtcNow().AddMinutes(-Math.Max(1, options.Value.IntervalMinutes) + 0.5);
        var terminals = await db.ContainerWatches.AsNoTracking()
            .Where(w => w.EndedAt == null && (w.LastCheckedAt == null || w.LastCheckedAt <= due))
            .Select(w => w.TerminalId).Distinct().ToListAsync(ct);
        var queued = 0;
        foreach (var terminalId in terminals)
        {
            try
            {
                queued += await CheckAsync(terminalId, w => w.LastCheckedAt == null || w.LastCheckedAt <= due, notify: true, ct);
            }
            catch (TosUnavailableException)
            {
                logger.LogInformation("Followed containers of terminal {TerminalId} not checked: the TOS is unavailable", terminalId);
            }
            catch (InvalidOperationException ex)
            {
                logger.LogWarning("Followed containers of terminal {TerminalId} not checked: {Reason}", terminalId, ex.Message);
            }
        }
        return queued;
    }

    private async Task<int> CheckAsync(int terminalId, System.Linq.Expressions.Expression<Func<ContainerWatch, bool>> which, bool notify, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var watches = await db.ContainerWatches.Where(w => w.TerminalId == terminalId && w.EndedAt == null).Where(which).ToListAsync(ct);
        if (watches.Count == 0)
            return 0;
        var terminalName = await db.Terminals.AsNoTracking().Where(t => t.TerminalId == terminalId).Select(t => t.Name).SingleAsync(ct);
        var users = await db.AppUsers.AsNoTracking().Where(u => watches.Select(w => w.UserId).Contains(u.UserId) && u.IsActive)
            .ToDictionaryAsync(u => u.UserId, u => u.PreferredLanguage, ct);

        var queued = 0;
        foreach (var batch in watches.Select(w => w.ContainerNo).Distinct().Chunk(ContainerLookupService.MaxContainers))
        {
            var states = (await StatesAsync(terminalId, batch.Select(c => (c, (string?)null)).ToList(), null, ct)).ToDictionary(s => s.ContainerNo);
            foreach (var w in watches.Where(w => states.ContainsKey(w.ContainerNo)))
            {
                var state = states[w.ContainerNo];
                var becameReady = state.State == ContainerState.Ready && w.LastState != ContainerState.Ready && w.LastState is not null;
                if (notify && becameReady && users.TryGetValue(w.UserId, out var language))
                {
                    db.NotificationLogs.Add(new NotificationLog
                    {
                        UserId = w.UserId,
                        Channel = NotificationChannel.Email,
                        TemplateCode = NotificationTemplates.ContainerReady,
                        Language = string.IsNullOrWhiteSpace(language) ? "en" : language.Trim().ToLowerInvariant(),
                        Status = NotificationStatus.Queued,
                        Params = JsonSerializer.Serialize(new Dictionary<string, string>
                        {
                            [NotificationText.Keys.Container] = w.ContainerNo,
                            [NotificationText.Keys.Terminal] = terminalName,
                        }),
                        CreatedAt = now,
                    });
                    w.ReadyNotifiedAt = now;
                    queued++;
                }
                w.LastState = state.State;
                w.LastMessage = Truncate(state.Problems.FirstOrDefault() ?? state.Warnings.FirstOrDefault(), 300);
                w.LastCheckedAt = now;
                // Done following: collected, or followed long enough.
                if (state.State == ContainerState.Collected || w.CreatedAt < now.AddDays(-options.Value.FollowDays))
                    w.EndedAt = now;
            }
        }
        await db.SaveChangesAsync(ct);
        return queued;
    }

    private static string? Truncate(string? value, int max) => value is null ? null : value.Length <= max ? value : value[..max];
}

/// <summary>Re-checks followed containers every Tracking:IntervalMinutes (default 15).</summary>
public sealed class TrackingWorker(IServiceScopeFactory scopes, IOptions<TrackingOptions> options, ILogger<TrackingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
            return;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ContainerTrackingService>().CheckFollowedAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Followed containers run failed");
            }
        }
    }
}

