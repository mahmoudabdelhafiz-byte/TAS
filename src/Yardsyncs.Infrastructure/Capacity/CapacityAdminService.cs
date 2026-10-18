using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Yardsyncs.Application.Auth;
using Yardsyncs.Application.Capacity;
using Yardsyncs.Application.Notifications;
using Yardsyncs.Domain;
using Yardsyncs.Domain.Booking;
using Yardsyncs.Domain.Capacity;
using Yardsyncs.Infrastructure.Notifications;
using Yardsyncs.Infrastructure.Persistence;

namespace Yardsyncs.Infrastructure.Capacity;

/// <summary>
/// The planner's configuration: templates, overrides, reservations, quota release rules, terminal settings.
/// Every change is written to the audit log in the same transaction, and changes that affect capacity
/// regenerate the affected slots straight away (bookings are kept; over-capacity is flagged).
/// Callers check permissions (AccessRules.CanManageCapacity) before calling the write methods.
/// </summary>
public sealed class CapacityAdminService(
    YardsyncsDbContext db,
    SlotGenerationService slotGeneration,
    TimeProvider clock,
    ILogger<CapacityAdminService> logger,
    IConfiguration? config = null)
{
    private static readonly JsonSerializerOptions AuditJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    // ================================================================ templates

    public async Task<IReadOnlyList<TemplateView>> ListTemplatesAsync(int terminalId, bool includeInactive, CancellationToken ct = default)
    {
        var codes = await CategoryCodesAsync(terminalId, ct);
        var templates = await db.CapacityTemplates.AsNoTracking()
            .Where(t => t.TerminalId == terminalId && (includeInactive || t.IsActive))
            .Include(t => t.Slots).ThenInclude(s => s.Quotas)
            .AsSplitQuery()
            .OrderByDescending(t => t.IsDefault).ThenBy(t => t.ValidFrom).ThenBy(t => t.TemplateId)
            .ToListAsync(ct);
        return templates.Select(t => CapacityConfigValidator.ToView(t, codes)).ToList();
    }

    public async Task<AdminResult<TemplateView>> CreateTemplateAsync(CurrentUser actor, int terminalId, TemplateInput input, CancellationToken ct = default)
    {
        var terminal = await FindTerminalAsync(terminalId, ct);
        if (terminal is null)
            return AdminResult<TemplateView>.NotFound($"Terminal {terminalId} not found.");

        var (template, errors) = CapacityConfigValidator.ValidateTemplate(input, await ActiveCategoryIdsAsync(terminalId, ct));
        if (template is null)
            return AdminResult<TemplateView>.Invalid(errors);

        if (template.IsDefault && await db.CapacityTemplates.AnyAsync(t => t.TerminalId == terminalId && t.IsDefault && t.IsActive, ct))
            return AdminResult<TemplateView>.Conflict("This terminal already has a default template; edit it instead.");

        template.TerminalId = terminalId;
        var codes = await CategoryCodesAsync(terminalId, ct);

        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            db.CapacityTemplates.Add(template);
            await db.SaveChangesAsync(ct);
            Audit(terminalId, "capacity_template", template.TemplateId, AuditAction.Insert, null,
                CapacityConfigValidator.ToView(template, codes), input.Reason, actor);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        var regenerated = await RegenerateAsync(terminal, Start(template), End(template), ct);
        return AdminResult<TemplateView>.Created(CapacityConfigValidator.ToView(template, codes), regenerated);
    }

    /// <summary>Replaces a template's name, dates, priority and all of its windows.</summary>
    public async Task<AdminResult<TemplateView>> ReplaceTemplateAsync(
        CurrentUser actor, int terminalId, int templateId, TemplateInput input, CancellationToken ct = default)
    {
        var terminal = await FindTerminalAsync(terminalId, ct);
        var template = terminal is null
            ? null
            : await db.CapacityTemplates
                .Include(t => t.Slots).ThenInclude(s => s.Quotas)
                .AsSplitQuery()
                .SingleOrDefaultAsync(t => t.TemplateId == templateId && t.TerminalId == terminalId, ct);
        if (terminal is null || template is null)
            return AdminResult<TemplateView>.NotFound($"Template {templateId} not found.");
        if (!template.IsActive)
            return AdminResult<TemplateView>.Conflict("This template is inactive.");
        if (input.IsDefault != template.IsDefault)
            return AdminResult<TemplateView>.Invalid("A template cannot switch between default and seasonal; create a new template instead.");

        var (validated, errors) = CapacityConfigValidator.ValidateTemplate(input, await ActiveCategoryIdsAsync(terminalId, ct));
        if (validated is null)
            return AdminResult<TemplateView>.Invalid(errors);

        var codes = await CategoryCodesAsync(terminalId, ct);
        var before = CapacityConfigValidator.ToView(template, codes);
        var oldStart = Start(template);
        var oldEnd = End(template);

        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            foreach (var slot in template.Slots.ToList())
                db.CapacityTemplateSlots.Remove(slot); // quotas go with it
            template.Slots.Clear();
            template.Slots.AddRange(validated.Slots);
            template.Name = validated.Name;
            template.ValidFrom = validated.ValidFrom;
            template.ValidTo = validated.ValidTo;
            template.Priority = validated.Priority;

            Audit(terminalId, "capacity_template", template.TemplateId, AuditAction.Update, before,
                CapacityConfigValidator.ToView(template, codes), input.Reason, actor);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        // A seasonal template that moved dates affects both the old and the new period.
        var regenerated = await RegenerateAsync(terminal, Min(oldStart, Start(template)), Max(oldEnd, End(template)), ct);
        return AdminResult<TemplateView>.Ok(CapacityConfigValidator.ToView(template, codes), regenerated);
    }

    public async Task<AdminResult<TemplateView>> DeactivateTemplateAsync(
        CurrentUser actor, int terminalId, int templateId, string? reason, CancellationToken ct = default)
    {
        var terminal = await FindTerminalAsync(terminalId, ct);
        var template = terminal is null
            ? null
            : await db.CapacityTemplates.Include(t => t.Slots).ThenInclude(s => s.Quotas).AsSplitQuery()
                .SingleOrDefaultAsync(t => t.TemplateId == templateId && t.TerminalId == terminalId, ct);
        if (terminal is null || template is null)
            return AdminResult<TemplateView>.NotFound($"Template {templateId} not found.");
        if (template.IsDefault)
            return AdminResult<TemplateView>.Conflict("The default template cannot be removed; edit it instead.");

        var codes = await CategoryCodesAsync(terminalId, ct);
        if (!template.IsActive)
            return AdminResult<TemplateView>.Ok(CapacityConfigValidator.ToView(template, codes));

        var before = CapacityConfigValidator.ToView(template, codes);
        template.IsActive = false;
        Audit(terminalId, "capacity_template", template.TemplateId, AuditAction.Delete, before, null, reason, actor);
        await db.SaveChangesAsync(ct);

        var regenerated = await RegenerateAsync(terminal, Start(template), End(template), ct);
        return AdminResult<TemplateView>.Ok(CapacityConfigValidator.ToView(template, codes), regenerated);
    }

    // ================================================================ overrides

    public async Task<IReadOnlyList<OverrideView>> ListOverridesAsync(
        int terminalId, DateTimeOffset? from, DateTimeOffset? to, bool includeInactive, CancellationToken ct = default)
    {
        var codes = await CategoryCodesAsync(terminalId, ct);
        var query = db.CapacityOverrides.AsNoTracking().Where(o => o.TerminalId == terminalId && (includeInactive || o.IsActive));
        if (from is { } f)
        {
            var fromUtc = f.ToUniversalTime();
            query = query.Where(o => o.EndAt > fromUtc);
        }
        if (to is { } t)
        {
            var toUtc = t.ToUniversalTime();
            query = query.Where(o => o.StartAt < toUtc);
        }

        var overrides = await query.OrderBy(o => o.StartAt).ThenBy(o => o.OverrideId).ToListAsync(ct);
        return overrides.Select(o => ToView(o, codes)).ToList();
    }

    public async Task<AdminResult<OverrideView>> CreateOverrideAsync(CurrentUser actor, int terminalId, OverrideInput input, CancellationToken ct = default)
    {
        var terminal = await FindTerminalAsync(terminalId, ct);
        if (terminal is null)
            return AdminResult<OverrideView>.NotFound($"Terminal {terminalId} not found.");

        var now = clock.GetUtcNow();
        var (item, errors) = CapacityConfigValidator.ValidateOverride(input, await ActiveCategoryIdsAsync(terminalId, ct), now);
        if (item is null)
            return AdminResult<OverrideView>.Invalid(errors);

        item.TerminalId = terminalId;
        item.CreatedBy = actor.UserId;
        item.CreatedAt = now;
        var codes = await CategoryCodesAsync(terminalId, ct);

        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            db.CapacityOverrides.Add(item);
            await db.SaveChangesAsync(ct);
            Audit(terminalId, "capacity_override", item.OverrideId, AuditAction.Insert, null, ToView(item, codes), item.Reason, actor);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        var regenerated = await RegenerateAsync(terminal, item.StartAt, item.EndAt, ct);
        if (item.Action == OverrideAction.Close)
            await NotifySlotClosedAsync(terminal, item, ct);
        logger.LogInformation("Override {Id} at terminal {TerminalId} by user {UserId}: {Action} {Value} ({Reason})",
            item.OverrideId, terminalId, actor.UserId, item.Action, item.Value, item.Reason);
        return AdminResult<OverrideView>.Created(ToView(item, codes), regenerated);
    }

    public async Task<AdminResult<OverrideView>> DeactivateOverrideAsync(
        CurrentUser actor, int terminalId, int overrideId, string? reason, CancellationToken ct = default)
    {
        var terminal = await FindTerminalAsync(terminalId, ct);
        var item = terminal is null
            ? null
            : await db.CapacityOverrides.SingleOrDefaultAsync(o => o.OverrideId == overrideId && o.TerminalId == terminalId, ct);
        if (terminal is null || item is null)
            return AdminResult<OverrideView>.NotFound($"Override {overrideId} not found.");

        var codes = await CategoryCodesAsync(terminalId, ct);
        if (!item.IsActive)
            return AdminResult<OverrideView>.Ok(ToView(item, codes));

        var before = ToView(item, codes);
        item.IsActive = false;
        Audit(terminalId, "capacity_override", item.OverrideId, AuditAction.Delete, before, null, reason, actor);
        await db.SaveChangesAsync(ct);

        var regenerated = await RegenerateAsync(terminal, item.StartAt, item.EndAt, ct);
        return AdminResult<OverrideView>.Ok(ToView(item, codes), regenerated);
    }

    // ================================================================ reservations

    public async Task<IReadOnlyList<ReservationView>> ListReservationsAsync(int terminalId, bool includeInactive, CancellationToken ct = default)
    {
        var codes = await CategoryCodesAsync(terminalId, ct);
        var items = await db.CapacityReservations.AsNoTracking()
            .Where(r => r.TerminalId == terminalId && (includeInactive || r.IsActive))
            .OrderBy(r => r.StartAt).ThenBy(r => r.ReservationId)
            .ToListAsync(ct);
        return items.Select(r => ToView(r, codes)).ToList();
    }

    public async Task<AdminResult<ReservationView>> CreateReservationAsync(
        CurrentUser actor, int terminalId, ReservationInput input, CancellationToken ct = default)
    {
        if (await FindTerminalAsync(terminalId, ct) is null)
            return AdminResult<ReservationView>.NotFound($"Terminal {terminalId} not found.");

        var (item, errors) = CapacityConfigValidator.ValidateReservation(input, await ActiveCategoryIdsAsync(terminalId, ct));
        if (item is null)
            return AdminResult<ReservationView>.Invalid(errors);
        if (item.CompanyId is { } companyId && !await db.Companies.AnyAsync(c => c.CompanyId == companyId, ct))
            return AdminResult<ReservationView>.Invalid($"Company {companyId} does not exist.");

        item.TerminalId = terminalId;
        var codes = await CategoryCodesAsync(terminalId, ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.CapacityReservations.Add(item);
        await db.SaveChangesAsync(ct);
        Audit(terminalId, "capacity_reservation", item.ReservationId, AuditAction.Insert, null, ToView(item, codes), input.Reason, actor);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return AdminResult<ReservationView>.Created(ToView(item, codes));
    }

    public async Task<AdminResult<ReservationView>> DeactivateReservationAsync(
        CurrentUser actor, int terminalId, int reservationId, string? reason, CancellationToken ct = default)
    {
        var item = await db.CapacityReservations.SingleOrDefaultAsync(r => r.ReservationId == reservationId && r.TerminalId == terminalId, ct);
        if (item is null)
            return AdminResult<ReservationView>.NotFound($"Reservation {reservationId} not found.");

        var codes = await CategoryCodesAsync(terminalId, ct);
        if (item.IsActive)
        {
            var before = ToView(item, codes);
            item.IsActive = false;
            Audit(terminalId, "capacity_reservation", item.ReservationId, AuditAction.Delete, before, null, reason, actor);
            await db.SaveChangesAsync(ct);
        }
        return AdminResult<ReservationView>.Ok(ToView(item, codes));
    }

    // ================================================================ quota release rules

    public async Task<IReadOnlyList<ReleaseRuleView>> GetReleaseRulesAsync(int terminalId, CancellationToken ct = default)
    {
        var codes = await CategoryCodesAsync(terminalId, ct);
        var rules = await db.QuotaReleaseRules.AsNoTracking().Where(r => r.TerminalId == terminalId).ToListAsync(ct);
        return rules
            .Where(r => codes.ContainsKey(r.CategoryId))
            .Select(r => new ReleaseRuleView(codes[r.CategoryId], r.HoursBefore))
            .OrderBy(r => r.Category)
            .ToList();
    }

    /// <summary>Replaces all release rules of the terminal (categories left out get no release).</summary>
    public async Task<AdminResult<IReadOnlyList<ReleaseRuleView>>> ReplaceReleaseRulesAsync(
        CurrentUser actor, int terminalId, IReadOnlyList<ReleaseRuleInput> input, string? reason, CancellationToken ct = default)
    {
        if (await FindTerminalAsync(terminalId, ct) is null)
            return AdminResult<IReadOnlyList<ReleaseRuleView>>.NotFound($"Terminal {terminalId} not found.");

        var (rules, errors) = CapacityConfigValidator.ValidateReleaseRules(input, await ActiveCategoryIdsAsync(terminalId, ct));
        if (errors.Count > 0)
            return AdminResult<IReadOnlyList<ReleaseRuleView>>.Invalid(errors);

        var before = await GetReleaseRulesAsync(terminalId, ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.QuotaReleaseRules.Where(r => r.TerminalId == terminalId).ExecuteDeleteAsync(ct);
        foreach (var rule in rules)
        {
            rule.TerminalId = terminalId;
            db.QuotaReleaseRules.Add(rule);
        }
        await db.SaveChangesAsync(ct);

        var after = await GetReleaseRulesAsync(terminalId, ct);
        Audit(terminalId, "quota_release_rule", terminalId, AuditAction.Update, before, after, reason, actor);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return AdminResult<IReadOnlyList<ReleaseRuleView>>.Ok(after);
    }

    // ================================================================ terminal settings

    public async Task<IReadOnlyDictionary<string, int>> GetSettingsAsync(int terminalId, CancellationToken ct = default)
    {
        var settings = TerminalSettings.From(
            await db.TerminalSettings.AsNoTracking().Where(s => s.TerminalId == terminalId).ToListAsync(ct));
        return new Dictionary<string, int>
        {
            [TerminalSettings.EarlyGraceKey] = settings.EarlyGraceMinutes,
            [TerminalSettings.LateGraceKey] = settings.LateGraceMinutes,
            [TerminalSettings.CancelCutoffKey] = settings.CancelCutoffMinutes,
            [TerminalSettings.MaxPerTruckPerDayKey] = settings.MaxAppointmentsPerTruckPerDay,
            [TerminalSettings.GateByNumberKey] = settings.GateByNumber ? 1 : 0,
            [TerminalSettings.GateByPlateKey] = settings.GateByPlate ? 1 : 0,
            [TerminalSettings.GateByContainerKey] = settings.GateByContainer ? 1 : 0,
            [TerminalSettings.DriverIdentityKey] = (int)settings.DriverIdentity,
            [TerminalSettings.TosGateWriteKey] = settings.TosGateWrite ? 1 : 0,
            [TerminalSettings.ModuleAutogateKey] = settings.Autogate ? 1 : 0,
            [TerminalSettings.SealHoldKey] = settings.SealHold ? 1 : 0,
            [TerminalSettings.FeeBookingKey] = (int)settings.FeeBooking,
            [TerminalSettings.FeeNoShowKey] = (int)settings.FeeNoShow,
            [TerminalSettings.FeeLateCancelKey] = (int)settings.FeeLateCancel,
            [TerminalSettings.LateCancelHoursKey] = settings.LateCancelHours,
            [TerminalSettings.ReportHourKey] = settings.ReportHour,
            [TerminalSettings.ReportWeekdayKey] = settings.ReportWeekday,
            [TerminalSettings.DriverPointsDaysKey] = settings.DriverPointsDays,
            [TerminalSettings.DriverWarnPointsKey] = settings.DriverWarnPoints,
            [TerminalSettings.DriverLimitPointsKey] = settings.DriverLimitPoints,
            [TerminalSettings.DriverRecordToCompanyKey] = settings.DriverRecordToCompany ? 1 : 0,
            [TerminalSettings.InductionModeKey] = settings.InductionMode,
            [TerminalSettings.SlotAdviceKey] = settings.SlotAdvice,
            [TerminalSettings.SlotTargetWaitKey] = settings.SlotTargetWaitMinutes,
            [TerminalSettings.SlotMaxOverbookKey] = settings.SlotMaxOverbookPct,
            [TerminalSettings.SlotHistoryWeeksKey] = settings.SlotHistoryWeeks,
        };
    }

    /// <summary>Updates the given settings; others keep their values.</summary>
    public async Task<AdminResult<IReadOnlyDictionary<string, int>>> UpdateSettingsAsync(
        CurrentUser actor, int terminalId, IReadOnlyDictionary<string, int> values, string? reason, CancellationToken ct = default)
    {
        if (await FindTerminalAsync(terminalId, ct) is null)
            return AdminResult<IReadOnlyDictionary<string, int>>.NotFound($"Terminal {terminalId} not found.");
        // Which modules a terminal has is what the customer bought: only system admins change it.
        if (values is not null && values.TryGetValue(TerminalSettings.ModuleAutogateKey, out var module) && !actor.IsSysAdmin
            && module != (await GetSettingsAsync(terminalId, ct))[TerminalSettings.ModuleAutogateKey])
            return AdminResult<IReadOnlyDictionary<string, int>>.Forbidden("Only system administrators switch modules on or off for a terminal.");

        var errors = CapacityConfigValidator.ValidateSettings(values);
        if (values is null || values.Count == 0)
            errors = ["No settings given."];
        if (errors.Count > 0)
            return AdminResult<IReadOnlyDictionary<string, int>>.Invalid(errors);

        var before = await GetSettingsAsync(terminalId, ct);
        var existing = await db.TerminalSettings.Where(s => s.TerminalId == terminalId).ToListAsync(ct);
        foreach (var (key, value) in values!)
        {
            var row = existing.FirstOrDefault(s => s.SettingKey == key);
            if (row is null)
                db.TerminalSettings.Add(new TerminalSetting { TerminalId = terminalId, SettingKey = key, SettingValue = value.ToString() });
            else
                row.SettingValue = value.ToString();
        }
        await db.SaveChangesAsync(ct);

        var after = await GetSettingsAsync(terminalId, ct);
        Audit(terminalId, "terminal_setting", terminalId, AuditAction.Update, before, after, reason, actor);
        await db.SaveChangesAsync(ct);
        return AdminResult<IReadOnlyDictionary<string, int>>.Ok(after);
    }

    // ================================================================ slot board & audit

    public async Task<IReadOnlyList<SlotBoardRow>> SlotBoardAsync(int terminalId, DateOnly localDate, CancellationToken ct = default)
    {
        var terminal = await FindTerminalAsync(terminalId, ct)
                       ?? throw new KeyNotFoundException($"Terminal {terminalId} not found.");
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(terminal.TimeZone);
        var dayStart = LocalDayStartUtc(localDate, timeZone);
        var dayEnd = LocalDayStartUtc(localDate.AddDays(1), timeZone);
        var codes = await CategoryCodesAsync(terminalId, ct);

        var slots = await db.Slots.AsNoTracking()
            .Include(s => s.Categories)
            .Where(s => s.TerminalId == terminalId && s.StartAt >= dayStart && s.StartAt < dayEnd)
            .OrderBy(s => s.StartAt)
            .ToListAsync(ct);

        return slots.Select(s => new SlotBoardRow(
            s.SlotId, s.StartAt, s.EndAt,
            $"{TimeZoneInfo.ConvertTime(s.StartAt, timeZone):HH:mm}–{TimeZoneInfo.ConvertTime(s.EndAt, timeZone):HH:mm}",
            s.TotalCapacity, s.TotalBooked, s.IsClosed, s.OverCapacity,
            s.Categories
                .Where(c => codes.ContainsKey(c.CategoryId))
                .OrderBy(c => codes[c.CategoryId])
                .Select(c => new SlotBoardCategory(codes[c.CategoryId], c.QuotaCapacity, c.Booked))
                .ToList())).ToList();
    }

    public async Task<IReadOnlyList<AuditEntryView>> AuditAsync(int terminalId, string? entity, int limit, CancellationToken ct = default)
    {
        var query = db.AuditLogs.AsNoTracking().Where(a => a.TerminalId == terminalId);
        if (!string.IsNullOrWhiteSpace(entity))
            query = query.Where(a => a.EntityName == entity);

        var rows = await query.OrderByDescending(a => a.ChangedAt).ThenByDescending(a => a.AuditId)
            .Take(Math.Clamp(limit, 1, 500))
            .ToListAsync(ct);
        return rows.Select(a => new AuditEntryView(
            a.AuditId, a.EntityName, a.EntityKey, a.Action.ToString().ToUpperInvariant(), a.OldValues, a.NewValues, a.Reason, a.ChangedBy, a.ChangedAt)).ToList();
    }

    // ================================================================ helpers

    /// <summary>
    /// After a CLOSE override, tells the drivers and dispatchers of appointments in the closed hours (or with moves
    /// in the closed category) that haven't left yet to reschedule. Their bookings are kept; they decide.
    /// </summary>
    private async Task NotifySlotClosedAsync(Terminal terminal, CapacityOverride closure, CancellationToken ct)
    {
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(terminal.TimeZone);
        var now = clock.GetUtcNow();

        var query = db.Appointments.AsNoTracking()
            .Where(a => a.TerminalId == terminal.TerminalId
                        && (a.Status == AppointmentStatus.Confirmed || a.Status == AppointmentStatus.EnRoute)
                        && a.Slot.StartAt < closure.EndAt && a.Slot.EndAt > closure.StartAt
                        && a.Slot.EndAt > now);
        query = closure.CategoryId is { } categoryId
            ? query.Where(a => a.Items.Any(i => i.CategoryId == categoryId))
            : query.Where(a => a.Slot.IsClosed);

        var affected = await query
            .Select(a => new { a.AppointmentId, a.AppointmentNo, a.Slot.StartAt, a.Slot.EndAt })
            .ToListAsync(ct);
        if (affected.Count == 0)
            return;

        var channel = NotificationQueue.DriverChannel(config, terminal.Code);
        foreach (var a in affected)
        {
            var values = new Dictionary<string, string>
            {
                [NotificationText.Keys.AppointmentNo] = a.AppointmentNo,
                [NotificationText.Keys.Slot] = NotificationText.SlotLabel(a.StartAt, a.EndAt, timeZone),
                [NotificationText.Keys.Reason] = closure.Reason,
            };
            await NotificationQueue.ForAppointmentAsync(db, a.AppointmentId, NotificationTemplates.SlotClosed, values, channel, now, ct,
                pushFirst: NotificationQueue.PushFirst(config));
        }
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Override {Id}: notified {Count} appointment(s) of the closure", closure.OverrideId, affected.Count);
    }

    private void Audit(int terminalId, string entity, long key, AuditAction action, object? before, object? after, string? reason, CurrentUser actor) =>
        db.AuditLogs.Add(new AuditLog
        {
            TerminalId = terminalId,
            EntityName = entity,
            EntityKey = key.ToString(),
            Action = action,
            OldValues = before is null ? null : JsonSerializer.Serialize(before, AuditJson),
            NewValues = after is null ? null : JsonSerializer.Serialize(after, AuditJson),
            Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim()[..Math.Min(reason.Trim().Length, 300)],
            ChangedBy = actor.UserId,
            ChangedAt = clock.GetUtcNow(),
        });

    private Task<Terminal?> FindTerminalAsync(int terminalId, CancellationToken ct) =>
        db.Terminals.AsNoTracking().SingleOrDefaultAsync(t => t.TerminalId == terminalId, ct);

    private async Task<IReadOnlyDictionary<string, int>> ActiveCategoryIdsAsync(int terminalId, CancellationToken ct) =>
        await db.TransactionCategories.AsNoTracking()
            .Where(c => c.TerminalId == terminalId && c.IsActive)
            .ToDictionaryAsync(c => c.Code.ToUpperInvariant(), c => c.CategoryId, ct);

    private async Task<IReadOnlyDictionary<int, string>> CategoryCodesAsync(int terminalId, CancellationToken ct) =>
        await db.TransactionCategories.AsNoTracking()
            .Where(c => c.TerminalId == terminalId)
            .ToDictionaryAsync(c => c.CategoryId, c => c.Code, ct);

    private static OverrideView ToView(CapacityOverride o, IReadOnlyDictionary<int, string> codes) =>
        new(o.OverrideId, o.StartAt, o.EndAt, o.CategoryId is { } c ? codes.GetValueOrDefault(c) : null,
            o.Action, o.Value, o.Reason, o.IsActive, o.CreatedBy, o.CreatedAt);

    private static ReservationView ToView(CapacityReservation r, IReadOnlyDictionary<int, string> codes) =>
        new(r.ReservationId, r.CompanyId, r.CategoryId is { } c ? codes.GetValueOrDefault(c) : null,
            r.StartAt, r.EndAt, r.UnitsPerSlot, r.ReleaseHoursBefore, r.IsActive);

    // Period a template can affect (null = open-ended, i.e. the default template).
    private static DateTimeOffset? Start(CapacityTemplate t) =>
        t.ValidFrom is { } d ? new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddDays(-1) : null;

    private static DateTimeOffset? End(CapacityTemplate t) =>
        t.ValidTo is { } d ? new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddDays(2) : null;

    private static DateTimeOffset? Min(DateTimeOffset? a, DateTimeOffset? b) => a is null || b is null ? null : a < b ? a : b;

    private static DateTimeOffset? Max(DateTimeOffset? a, DateTimeOffset? b) => a is null || b is null ? null : a > b ? a : b;

    /// <summary>Regenerates the terminal's slots for the part of [from, to) inside today..booking horizon.</summary>
    private async Task<SlotGenerationResult?> RegenerateAsync(Terminal terminal, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct)
    {
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(terminal.TimeZone);
        DateOnly LocalDate(DateTimeOffset instant) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, timeZone).DateTime);

        var today = LocalDate(clock.GetUtcNow());
        var horizonEnd = today.AddDays(terminal.BookingHorizonDays);

        var start = from is { } f && LocalDate(f) > today ? LocalDate(f) : today;
        var end = to is { } t && LocalDate(t) < horizonEnd ? LocalDate(t) : horizonEnd;
        if (end < start)
            return null;

        return await slotGeneration.GenerateAsync(terminal.TerminalId, start, end, ct);
    }

    private static DateTimeOffset LocalDayStartUtc(DateOnly date, TimeZoneInfo timeZone) =>
        new(TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), timeZone), TimeSpan.Zero);
}
