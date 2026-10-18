using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yardsyncs.Application.Auth;
using Yardsyncs.Application.Capacity;
using Yardsyncs.Domain;
using Yardsyncs.Domain.Capacity;
using Yardsyncs.Infrastructure.Persistence;

namespace Yardsyncs.Infrastructure.Capacity;

/// <summary>
/// The planner's suggestions (AI-optimised slots, step 1): computed from the terminal's history on request;
/// approving one changes the default template window or adds a capacity override through the usual planner
/// service (audited, slots regenerated); dismissing one hides it for four weeks. Both are kept in the audit log.
/// </summary>
public sealed class SlotAdviceService(YardsyncsDbContext db, CapacityAdminService capacity, TimeProvider clock)
{
    public const string AuditEntity = "slot_advice";
    public static readonly TimeSpan DismissFor = TimeSpan.FromDays(28);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record DecisionEntry(string Decision, string Kind, decimal Current, decimal Suggested);

    /// <exception cref="KeyNotFoundException">Unknown terminal.</exception>
    public async Task<SlotAdvice> AdviceAsync(int terminalId, CancellationToken ct = default)
    {
        var terminal = await db.Terminals.AsNoTracking().SingleOrDefaultAsync(t => t.TerminalId == terminalId, ct)
                       ?? throw new KeyNotFoundException($"Terminal {terminalId} not found.");
        var settings = TerminalSettings.From(await db.TerminalSettings.AsNoTracking().Where(s => s.TerminalId == terminalId).ToListAsync(ct));
        var advice = new AdviceSettings(settings.SlotAdvice, settings.SlotTargetWaitMinutes, settings.SlotMaxOverbookPct, settings.SlotHistoryWeeks);
        if (advice.Mode == 0)
            return new SlotAdvice(0, 0, 0, false, []);

        var now = clock.GetUtcNow();
        var from = now.AddDays(-7 * advice.HistoryWeeks);
        var until = now.AddDays(SlotAdvisor.ForecastDays + 1);
        var template = await DefaultTemplateAsync(terminalId, ct);
        var windows = template?.Windows
            .Select(w => new AdviceWindow(w.Day, SlotAdvisor.ParseTime(w.Start), SlotAdvisor.ParseTime(w.End), w.TotalCapacity, w.OverbookPct))
            .ToList() ?? new List<AdviceWindow>();

        var visits = await db.Appointments.AsNoTracking()
            .Where(a => a.TerminalId == terminalId && a.Slot.StartAt >= from && a.Slot.StartAt < now)
            .Select(a => new { a.Slot.StartAt, a.Status, a.ArrivedAt, a.GateInAt })
            .ToListAsync(ct);
        var slots = await db.Slots.AsNoTracking()
            .Where(s => s.TerminalId == terminalId && s.StartAt >= from && s.StartAt < until)
            .Select(s => new { s.StartAt, s.TotalCapacity, s.TotalBooked, s.IsClosed })
            .ToListAsync(ct);

        var result = SlotAdvisor.Compute(
            windows,
            visits.Select(v => new AdviceVisit(v.StartAt, v.Status, v.ArrivedAt, v.GateInAt)).ToList(),
            slots.Where(s => s.StartAt < now).Select(s => new AdviceSlot(s.StartAt, s.TotalCapacity, s.TotalBooked, s.IsClosed)).ToList(),
            slots.Where(s => s.StartAt >= now).Select(s => new AdviceSlot(s.StartAt, s.TotalCapacity, s.TotalBooked, s.IsClosed)).ToList(),
            advice, TimeZoneInfo.FindSystemTimeZoneById(terminal.TimeZone), now);

        // Dismissed in the last four weeks with the same suggested value: not shown again (a new value is).
        var since = now - DismissFor;
        var decisions = await db.AuditLogs.AsNoTracking()
            .Where(a => a.TerminalId == terminalId && a.EntityName == AuditEntity && a.ChangedAt >= since)
            .Select(a => new { a.EntityKey, a.NewValues })
            .ToListAsync(ct);
        var dismissed = decisions
            .Select(d => (d.EntityKey, Value: d.NewValues is null ? null : JsonSerializer.Deserialize<DecisionEntry>(d.NewValues, Json)))
            .Where(d => d.Value?.Decision == "Dismissed")
            .Select(d => (d.EntityKey, d.Value!.Suggested))
            .ToHashSet();
        return result with { Suggestions = result.Suggestions.Where(s => !dismissed.Contains((s.Key, s.Suggested))).ToList() };
    }

    /// <summary>Approves (applies) or dismisses a suggestion that is still current.</summary>
    public async Task<AdminResult<SlotSuggestion>> DecideAsync(CurrentUser actor, int terminalId, string key, bool approve, CancellationToken ct = default)
    {
        SlotAdvice advice;
        try
        {
            advice = await AdviceAsync(terminalId, ct);
        }
        catch (KeyNotFoundException e)
        {
            return AdminResult<SlotSuggestion>.NotFound(e.Message);
        }
        var suggestion = advice.Suggestions.FirstOrDefault(s => s.Key == key);
        if (suggestion is null)
            return AdminResult<SlotSuggestion>.NotFound("This suggestion is no longer current. Reload the suggestions.");

        var reason = Reason(suggestion);
        if (approve)
        {
            var problem = suggestion.Kind == AdviceKind.BusyDay
                ? await AddCapacityAsync(actor, terminalId, suggestion, reason, ct)
                : await ChangeWindowAsync(actor, terminalId, suggestion, reason, ct);
            if (problem is { } p)
                return new AdminResult<SlotSuggestion>(p.Status, default, p.Errors);
        }

        db.AuditLogs.Add(new AuditLog
        {
            TerminalId = terminalId,
            EntityName = AuditEntity,
            EntityKey = suggestion.Key,
            Action = AuditAction.Insert,
            NewValues = JsonSerializer.Serialize(
                new DecisionEntry(approve ? "Approved" : "Dismissed", suggestion.Kind.ToString(), suggestion.Current, suggestion.Suggested), Json),
            Reason = reason,
            ChangedBy = actor.UserId,
            ChangedAt = clock.GetUtcNow(),
        });
        await db.SaveChangesAsync(ct);
        return AdminResult<SlotSuggestion>.Ok(suggestion);
    }

    private async Task<TemplateView?> DefaultTemplateAsync(int terminalId, CancellationToken ct) =>
        (await capacity.ListTemplatesAsync(terminalId, includeInactive: false, ct)).FirstOrDefault(t => t.IsDefault && t.IsActive);

    /// <summary>The window's overbooking or trucks per slot in the default template (null when done).</summary>
    private async Task<(AdminStatus Status, IReadOnlyList<string> Errors)?> ChangeWindowAsync(
        CurrentUser actor, int terminalId, SlotSuggestion s, string reason, CancellationToken ct)
    {
        var template = await DefaultTemplateAsync(terminalId, ct);
        if (template is null)
            return (AdminStatus.Conflict, new[] { "The terminal has no default capacity template." });
        var found = false;
        var windows = template.Windows.Select(w =>
        {
            if (w.Day != s.Day || w.Start != s.Start)
                return new TemplateWindowInput(w.Day, w.Start, w.End, w.TotalCapacity, w.OverbookPct, w.Quotas);
            found = true;
            return s.Kind == AdviceKind.Overbook
                ? new TemplateWindowInput(w.Day, w.Start, w.End, w.TotalCapacity, s.Suggested, w.Quotas)
                : new TemplateWindowInput(w.Day, w.Start, w.End, (int)s.Suggested, w.OverbookPct, w.Quotas);
        }).ToList();
        if (!found)
            return (AdminStatus.Conflict, new[] { "The template window changed since the suggestion was made. Reload the suggestions." });

        var result = await capacity.ReplaceTemplateAsync(actor, terminalId, template.TemplateId,
            new TemplateInput(template.Name, template.IsDefault, template.ValidFrom, template.ValidTo, template.Priority, windows, reason), ct);
        return result.Status == AdminStatus.Ok ? null : (result.Status, result.Errors);
    }

    /// <summary>More trucks on a busy day: an ADJUST override over that day, spread over its open slots.</summary>
    private async Task<(AdminStatus Status, IReadOnlyList<string> Errors)?> AddCapacityAsync(
        CurrentUser actor, int terminalId, SlotSuggestion s, string reason, CancellationToken ct)
    {
        var terminal = await db.Terminals.AsNoTracking().SingleAsync(t => t.TerminalId == terminalId, ct);
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(terminal.TimeZone);
        DateTimeOffset StartOf(DateOnly d)
        {
            var local = d.ToDateTime(TimeOnly.MinValue);
            return new DateTimeOffset(local, timeZone.GetUtcOffset(local)).ToUniversalTime();
        }

        var date = s.Date!.Value;
        var start = StartOf(date);
        var end = StartOf(date.AddDays(1));
        var open = await db.Slots.AsNoTracking().CountAsync(x => x.TerminalId == terminalId && x.StartAt >= start && x.StartAt < end && !x.IsClosed, ct);
        if (open == 0)
            return (AdminStatus.Conflict, new[] { "That day has no open slots." });
        var perSlot = (int)Math.Ceiling((s.Suggested - s.Current) / open);
        var result = await capacity.CreateOverrideAsync(actor, terminalId,
            new OverrideInput(start, end, null, OverrideAction.Adjust, Math.Max(1, perSlot), reason), ct);
        return result.Status is AdminStatus.Ok or AdminStatus.Created ? null : (result.Status, result.Errors);
    }

    /// <summary>The audit reason, in English (the screen shows its own words).</summary>
    public static string Reason(SlotSuggestion s) => s.Kind switch
    {
        AdviceKind.Overbook => $"Suggestion: {s.Day} {s.Start}–{s.End} overbooking {s.Current:0.#}% → {s.Suggested:0.#}% ({s.NoShowRate:P0} no-shows over {s.Sample} visits).",
        AdviceKind.LowerCapacity => $"Suggestion: {s.Day} {s.Start}–{s.End} {s.Current:0} → {s.Suggested:0} trucks per slot (gate wait {s.AvgWaitMinutes:0} min).",
        AdviceKind.RaiseCapacity => $"Suggestion: {s.Day} {s.Start}–{s.End} {s.Current:0} → {s.Suggested:0} trucks per slot ({s.Utilization:P0} full, gate wait {s.AvgWaitMinutes:0} min).",
        _ => $"Suggestion: {s.Date:yyyy-MM-dd} expected {s.Forecast} trucks against {s.Current:0}; capacity → {s.Suggested:0}.",
    };
}
