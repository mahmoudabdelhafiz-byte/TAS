using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yardsyncs.Application.Auth;
using Yardsyncs.Application.Capacity;
using Yardsyncs.Application.Safety;
using Yardsyncs.Domain;
using Yardsyncs.Infrastructure.Persistence;

namespace Yardsyncs.Infrastructure.DriverApp;

/// <summary>
/// Safety in the driver app: the terminal's campaigns (notices the driver reads and confirms) and its induction (a short
/// course with questions, valid for some months). A driver is one person (mobile number) across companies. The gate
/// mentions drivers who haven't confirmed a campaign, and warns about — or refers — drivers without a valid induction.
/// </summary>
public sealed class SafetyService(YardsyncsDbContext db, TimeProvider clock)
{
    /// <summary>A driver gets campaigns and inductions of terminals they visited in this many days, or will visit.</summary>
    public const int RecentDays = 60;

    // ================================================================ campaigns (terminal)

    public async Task<IReadOnlyList<CampaignView>> CampaignsAsync(int terminalId, CancellationToken ct = default)
    {
        var today = await TodayAsync(terminalId, ct);
        var rows = await db.SafetyCampaigns.AsNoTracking().Where(c => c.TerminalId == terminalId)
            .OrderByDescending(c => c.ArchivedAt == null).ThenByDescending(c => c.StartsOn)
            .Select(c => new { c, Confirmed = db.SafetyAcks.Count(a => a.CampaignId == c.CampaignId) })
            .Take(200)
            .ToListAsync(ct);
        return rows.Select(r => View(r.c, r.Confirmed, today)).ToList();
    }

    public async Task<AdminResult<CampaignView>> SaveCampaignAsync(CurrentUser actor, int terminalId, int? campaignId, CampaignInput input, CancellationToken ct = default)
    {
        if (SafetyRules.CheckCampaign(input) is { } error)
            return AdminResult<CampaignView>.Invalid(error);
        SafetyCampaign? row;
        if (campaignId is { } id)
        {
            row = await db.SafetyCampaigns.SingleOrDefaultAsync(c => c.CampaignId == id && c.TerminalId == terminalId, ct);
            if (row is null)
                return AdminResult<CampaignView>.NotFound($"Campaign {id} not found.");
        }
        else
        {
            row = new SafetyCampaign { TerminalId = terminalId, CreatedBy = actor.UserId, CreatedAt = clock.GetUtcNow() };
            db.SafetyCampaigns.Add(row);
        }
        row.Texts = JsonSerializer.Serialize(Clean(input.Texts), SafetyRules.Json);
        row.ImageUrl = string.IsNullOrWhiteSpace(input.ImageUrl) ? null : input.ImageUrl.Trim();
        row.StartsOn = input.StartsOn;
        row.EndsOn = input.EndsOn;
        row.CompanyIds = input.CompanyIds is { Count: > 0 } ids ? ids.Distinct().ToArray() : null;
        row.RequireAck = input.RequireAck;
        await db.SaveChangesAsync(ct);
        var view = View(row, await db.SafetyAcks.CountAsync(a => a.CampaignId == row.CampaignId, ct), await TodayAsync(terminalId, ct));
        return campaignId is null ? AdminResult<CampaignView>.Created(view) : AdminResult<CampaignView>.Ok(view);
    }

    /// <summary>Ends a campaign now (it stays listed, with its confirmations).</summary>
    public async Task<AdminResult<CampaignView>> ArchiveAsync(int terminalId, int campaignId, CancellationToken ct = default)
    {
        var row = await db.SafetyCampaigns.SingleOrDefaultAsync(c => c.CampaignId == campaignId && c.TerminalId == terminalId, ct);
        if (row is null)
            return AdminResult<CampaignView>.NotFound($"Campaign {campaignId} not found.");
        row.ArchivedAt ??= clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return AdminResult<CampaignView>.Ok(View(row, await db.SafetyAcks.CountAsync(a => a.CampaignId == campaignId, ct), await TodayAsync(terminalId, ct)));
    }

    // ================================================================ induction (terminal)

    public async Task<InductionView?> InductionAsync(int terminalId, CancellationToken ct = default)
    {
        var row = await db.SafetyInductions.AsNoTracking().SingleOrDefaultAsync(i => i.TerminalId == terminalId, ct);
        if (row is null)
            return null;
        var now = clock.GetUtcNow();
        var valid = await db.InductionPasses.Where(p => p.TerminalId == terminalId && p.ValidUntil > now).Select(p => p.Mobile).Distinct().CountAsync(ct);
        return new InductionView(Pages(row), Questions(row), row.ValidMonths, row.Version, row.UpdatedAt, valid);
    }

    /// <summary>Saves the course; a changed course gets a new version (passes of earlier versions stay valid until they end).</summary>
    public async Task<AdminResult<InductionView>> SaveInductionAsync(CurrentUser actor, int terminalId, InductionInput input, CancellationToken ct = default)
    {
        if (SafetyRules.CheckInduction(input) is { } error)
            return AdminResult<InductionView>.Invalid(error);
        var row = await db.SafetyInductions.SingleOrDefaultAsync(i => i.TerminalId == terminalId, ct);
        var created = row is null;
        if (row is null)
        {
            row = new SafetyInduction { TerminalId = terminalId, Version = 0 };
            db.SafetyInductions.Add(row);
        }
        row.Pages = JsonSerializer.Serialize(input.Pages.Select(p => new InductionPage(Clean(p.Texts))).ToList(), SafetyRules.Json);
        row.Questions = JsonSerializer.Serialize(input.Questions, SafetyRules.Json);
        row.ValidMonths = input.ValidMonths;
        row.Version++;
        row.UpdatedBy = actor.UserId;
        row.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        var view = (await InductionAsync(terminalId, ct))!;
        return created ? AdminResult<InductionView>.Created(view) : AdminResult<InductionView>.Ok(view);
    }

    // ================================================================ the driver (app)

    /// <summary>Campaigns running today at the terminals the driver visits, for their companies, in the app's language.</summary>
    public async Task<IReadOnlyList<DriverCampaignView>> CampaignsForDriverAsync(CurrentDriver driver, string language, CancellationToken ct = default)
    {
        var (terminals, companies) = await PersonAsync(driver, ct);
        if (terminals.Count == 0)
            return [];
        var acked = (await db.SafetyAcks.AsNoTracking().Where(a => a.Mobile == driver.Mobile).Select(a => a.CampaignId).ToListAsync(ct)).ToHashSet();
        var result = new List<DriverCampaignView>();
        foreach (var (terminalId, name) in terminals)
        {
            var today = await TodayAsync(terminalId, ct);
            var rows = await db.SafetyCampaigns.AsNoTracking()
                .Where(c => c.TerminalId == terminalId && c.ArchivedAt == null && c.StartsOn <= today && c.EndsOn >= today)
                .OrderByDescending(c => c.StartsOn)
                .ToListAsync(ct);
            foreach (var c in rows.Where(c => c.CompanyIds is null || c.CompanyIds.Any(companies.Contains)))
            {
                var text = SafetyRules.Pick(Texts(c), language);
                result.Add(new DriverCampaignView(c.CampaignId, name, text.Title, text.Body, c.ImageUrl, c.RequireAck, acked.Contains(c.CampaignId), c.EndsOn));
            }
        }
        return result.OrderBy(c => c.Confirmed).ToList();
    }

    public async Task<AdminResult<IReadOnlyList<DriverCampaignView>>> ConfirmAsync(CurrentDriver driver, int campaignId, string language, CancellationToken ct = default)
    {
        var visible = await CampaignsForDriverAsync(driver, language, ct);
        if (visible.All(c => c.CampaignId != campaignId))
            return AdminResult<IReadOnlyList<DriverCampaignView>>.NotFound($"Campaign {campaignId} not found.");
        if (!await db.SafetyAcks.AnyAsync(a => a.CampaignId == campaignId && a.Mobile == driver.Mobile, ct))
        {
            db.SafetyAcks.Add(new SafetyAck { CampaignId = campaignId, Mobile = driver.Mobile, DriverId = driver.DriverId, AckedAt = clock.GetUtcNow() });
            await db.SaveChangesAsync(ct);
        }
        return AdminResult<IReadOnlyList<DriverCampaignView>>.Ok(await CampaignsForDriverAsync(driver, language, ct));
    }

    /// <summary>The inductions of the terminals the driver visits, in the app's language, and whether each is needed.</summary>
    public async Task<IReadOnlyList<DriverInductionView>> InductionsForDriverAsync(CurrentDriver driver, string language, CancellationToken ct = default)
    {
        var (terminals, _) = await PersonAsync(driver, ct);
        var ids = terminals.Select(t => t.TerminalId).ToList();
        var rows = await db.SafetyInductions.AsNoTracking().Where(i => ids.Contains(i.TerminalId)).ToListAsync(ct);
        var now = clock.GetUtcNow();
        var passes = await db.InductionPasses.AsNoTracking()
            .Where(p => ids.Contains(p.TerminalId) && p.Mobile == driver.Mobile)
            .GroupBy(p => p.TerminalId)
            .Select(g => new { TerminalId = g.Key, ValidUntil = g.Max(p => p.ValidUntil) })
            .ToDictionaryAsync(p => p.TerminalId, p => p.ValidUntil, ct);
        return rows.Select(i =>
        {
            DateTimeOffset? until = passes.TryGetValue(i.TerminalId, out var u) ? u : null;
            return new DriverInductionView(i.TerminalId, terminals.First(t => t.TerminalId == i.TerminalId).Name, i.Version,
                Pages(i).Select(p => SafetyRules.Pick(p.Texts, language)).ToList(),
                Questions(i).Select(q => SafetyRules.Pick(q.Texts, language)).ToList(),
                until, until is null || until <= now);
        }).ToList();
    }

    /// <summary>Marks the driver's answers; all right passes the induction for the terminal's months.</summary>
    public async Task<AdminResult<AnswerResult>> AnswerAsync(CurrentDriver driver, int terminalId, AnswersInput input, CancellationToken ct = default)
    {
        var (terminals, _) = await PersonAsync(driver, ct);
        var induction = terminals.Any(t => t.TerminalId == terminalId)
            ? await db.SafetyInductions.AsNoTracking().SingleOrDefaultAsync(i => i.TerminalId == terminalId, ct)
            : null;
        if (induction is null)
            return AdminResult<AnswerResult>.NotFound($"No induction for terminal {terminalId}.");
        var wrong = SafetyRules.Wrong(Questions(induction), input.Answers);
        if (wrong.Count > 0)
            return AdminResult<AnswerResult>.Ok(new AnswerResult(false, wrong, null));
        var now = clock.GetUtcNow();
        var until = now.AddMonths(induction.ValidMonths);
        db.InductionPasses.Add(new InductionPass
        {
            TerminalId = terminalId, Mobile = driver.Mobile, DriverId = driver.DriverId, Version = induction.Version, PassedAt = now, ValidUntil = until,
        });
        await db.SaveChangesAsync(ct);
        return AdminResult<AnswerResult>.Ok(new AnswerResult(true, [], until));
    }

    // ================================================================ the gate

    /// <summary>
    /// For the gate: a line about a missing or expired induction (with whether to refer, per the terminal's setting) and
    /// lines about campaigns the driver hasn't confirmed.
    /// </summary>
    public static async Task<(string? Induction, bool Refer, IReadOnlyList<string> Campaigns)> GateCheckAsync(
        YardsyncsDbContext db, int terminalId, int driverId, string driverName, TerminalSettings settings, DateTimeOffset now, CancellationToken ct)
    {
        var person = await db.Drivers.AsNoTracking().Where(d => d.DriverId == driverId).Select(d => new { d.Mobile, d.CompanyId }).SingleOrDefaultAsync(ct);
        if (person is null)
            return (null, false, Array.Empty<string>());

        string? induction = null;
        if (settings.InductionMode > 0 && await db.SafetyInductions.AnyAsync(i => i.TerminalId == terminalId, ct)
            && !await db.InductionPasses.AnyAsync(p => p.TerminalId == terminalId && p.Mobile == person.Mobile && p.ValidUntil > now, ct))
            induction = $"Driver {driverName} has no valid safety induction for this terminal.";

        var zone = await db.Terminals.AsNoTracking().Where(t => t.TerminalId == terminalId).Select(t => t.TimeZone).SingleAsync(ct);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById(zone)).DateTime);
        var companies = await db.Drivers.AsNoTracking().Where(d => d.Mobile == person.Mobile).Select(d => d.CompanyId).ToListAsync(ct);
        var open = await db.SafetyCampaigns.AsNoTracking()
            .Where(c => c.TerminalId == terminalId && c.RequireAck && c.ArchivedAt == null && c.StartsOn <= today && c.EndsOn >= today
                        && !db.SafetyAcks.Any(a => a.CampaignId == c.CampaignId && a.Mobile == person.Mobile))
            .ToListAsync(ct);
        var campaigns = open.Where(c => c.CompanyIds is null || c.CompanyIds.Any(companies.Contains))
            .Select(c => $"Driver {driverName} has not confirmed the safety notice \"{SafetyRules.Pick(Texts(c), "en").Title}\".")
            .ToList();
        return (induction, settings.InductionMode == 2, campaigns);
    }

    // ================================================================ helpers

    /// <summary>The terminals the person visited in the last <see cref="RecentDays"/> days or will visit, and their companies.</summary>
    private async Task<(IReadOnlyList<(int TerminalId, string Name)> Terminals, HashSet<int> Companies)> PersonAsync(CurrentDriver driver, CancellationToken ct)
    {
        var since = clock.GetUtcNow().AddDays(-RecentDays);
        var people = await db.Drivers.AsNoTracking().Where(d => d.Mobile == driver.Mobile && !d.IsBlocked).Select(d => new { d.DriverId, d.CompanyId }).ToListAsync(ct);
        var driverIds = people.Select(p => p.DriverId).ToList();
        var terminals = await db.Appointments.AsNoTracking()
            .Where(a => a.DriverId != null && driverIds.Contains(a.DriverId.Value) && a.Slot.EndAt >= since)
            .Select(a => new { a.TerminalId, Name = db.Terminals.Where(t => t.TerminalId == a.TerminalId).Select(t => t.Name).FirstOrDefault() ?? "" })
            .Distinct()
            .ToListAsync(ct);
        return (terminals.Select(t => (t.TerminalId, t.Name)).ToList(), people.Select(p => p.CompanyId).ToHashSet());
    }

    private async Task<DateOnly> TodayAsync(int terminalId, CancellationToken ct)
    {
        var zone = await db.Terminals.AsNoTracking().Where(t => t.TerminalId == terminalId).Select(t => t.TimeZone).SingleAsync(ct);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), TimeZoneInfo.FindSystemTimeZoneById(zone)).DateTime);
    }

    private static Dictionary<string, LocalizedText> Clean(IReadOnlyDictionary<string, LocalizedText> texts) =>
        texts.ToDictionary(t => t.Key, t => new LocalizedText(t.Value.Title.Trim(), t.Value.Body.Trim()));

    private static IReadOnlyDictionary<string, LocalizedText> Texts(SafetyCampaign c) =>
        JsonSerializer.Deserialize<Dictionary<string, LocalizedText>>(c.Texts, SafetyRules.Json) ?? new Dictionary<string, LocalizedText>();

    private static IReadOnlyList<InductionPage> Pages(SafetyInduction i) =>
        JsonSerializer.Deserialize<List<InductionPage>>(i.Pages, SafetyRules.Json) ?? [];

    private static IReadOnlyList<InductionQuestion> Questions(SafetyInduction i) =>
        JsonSerializer.Deserialize<List<InductionQuestion>>(i.Questions, SafetyRules.Json) ?? [];

    private static CampaignView View(SafetyCampaign c, int confirmed, DateOnly today) =>
        new(c.CampaignId, Texts(c), c.ImageUrl, c.StartsOn, c.EndsOn, c.CompanyIds, c.RequireAck, c.CreatedAt, c.ArchivedAt is not null,
            c.ArchivedAt is null && c.StartsOn <= today && c.EndsOn >= today, confirmed);
}
