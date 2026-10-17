using System.Globalization;
using Yardsyncs.Domain;
using Yardsyncs.Domain.Capacity;

namespace Yardsyncs.Application.Capacity;

/// <summary>Checks planner input and turns it into domain objects. Pure: no database.</summary>
public static class CapacityConfigValidator
{
    public const int MaxCapacityPerSlot = 1000;
    public const decimal MaxOverbookPct = 50;
    public const int MaxOverrideDays = 92;
    public const int MaxReleaseHours = 168;

    private static readonly TimeOnly EndOfDay = new(23, 59);

    /// <summary>Allowed terminal settings and their valid ranges.</summary>
    public static readonly IReadOnlyDictionary<string, (int Min, int Max)> SettingRanges = new Dictionary<string, (int, int)>
    {
        [TerminalSettings.EarlyGraceKey] = (0, 240),
        [TerminalSettings.LateGraceKey] = (0, 240),
        [TerminalSettings.CancelCutoffKey] = (0, 1440),
        [TerminalSettings.MaxPerTruckPerDayKey] = (0, 50),
        [TerminalSettings.GateByNumberKey] = (0, 1),
        [TerminalSettings.GateByPlateKey] = (0, 1),
        [TerminalSettings.GateByContainerKey] = (0, 1),
        [TerminalSettings.DriverIdentityKey] = (0, 2),
        [TerminalSettings.TosGateWriteKey] = (0, 1),
        [TerminalSettings.ModuleAutogateKey] = (0, 1),
        [TerminalSettings.SealHoldKey] = (0, 1),
        [TerminalSettings.FeeBookingKey] = (0, 100_000_000),
        [TerminalSettings.FeeNoShowKey] = (0, 100_000_000),
        [TerminalSettings.FeeLateCancelKey] = (0, 100_000_000),
        [TerminalSettings.LateCancelHoursKey] = (0, 168),
    };

    // ------------------------------------------------------------ templates

    /// <param name="categoryIds">Active categories of the terminal by upper-case code.</param>
    public static (CapacityTemplate? Template, IReadOnlyList<string> Errors) ValidateTemplate(
        TemplateInput input, IReadOnlyDictionary<string, int> categoryIds)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Trim().Length > 100)
            errors.Add("Name is required (max 100 characters).");

        if (input.IsDefault)
        {
            if (input.ValidFrom is not null || input.ValidTo is not null)
                errors.Add("The default template has no dates; create a seasonal template for a date range.");
        }
        else if (input.ValidFrom is null || input.ValidTo is null)
        {
            errors.Add("A seasonal template needs validFrom and validTo.");
        }
        else if (input.ValidTo < input.ValidFrom)
        {
            errors.Add("validTo must be on or after validFrom.");
        }

        if (input.Windows is null || input.Windows.Count == 0)
            errors.Add("At least one time window is required.");

        var slots = new List<CapacityTemplateSlot>();
        var windows = input.Windows ?? Array.Empty<TemplateWindowInput>();
        for (var i = 0; i < windows.Count; i++)
        {
            var w = windows[i];
            var label = $"Window {i + 1} ({w.Day} {w.Start}–{w.End})";

            if (!Enum.IsDefined(w.Day))
            {
                errors.Add($"Window {i + 1}: invalid day.");
                continue;
            }

            var start = ParseTime(w.Start);
            var end = ParseEnd(w.End);
            if (start is null || end is null)
            {
                errors.Add($"{label}: times must be HH:mm (end may be 24:00).");
                continue;
            }
            if (end <= start)
                errors.Add($"{label}: end must be after start.");
            if (w.TotalCapacity is < 0 or > MaxCapacityPerSlot)
                errors.Add($"{label}: total capacity must be 0–{MaxCapacityPerSlot}.");
            if (w.OverbookPct is < 0 or > MaxOverbookPct)
                errors.Add($"{label}: overbooking must be 0–{MaxOverbookPct}%.");

            var quotas = new List<CapacityTemplateQuota>();
            foreach (var (code, max) in w.Quotas ?? new Dictionary<string, int>())
            {
                if (!categoryIds.TryGetValue(code.Trim().ToUpperInvariant(), out var categoryId))
                    errors.Add($"{label}: unknown category '{code}'.");
                else if (max < 0 || max > MaxCapacityPerSlot)
                    errors.Add($"{label}: quota for {code} must be 0–{MaxCapacityPerSlot}.");
                else
                    quotas.Add(new CapacityTemplateQuota { CategoryId = categoryId, MaxUnits = max });
            }

            slots.Add(new CapacityTemplateSlot
            {
                Day = w.Day,
                StartTime = start.Value,
                EndTime = end.Value,
                TotalCapacity = w.TotalCapacity,
                OverbookPct = w.OverbookPct,
                Quotas = quotas,
            });
        }

        // Windows on the same day must not overlap — otherwise it is unclear which capacity applies.
        foreach (var day in slots.GroupBy(s => s.Day))
        {
            var ordered = day.OrderBy(s => s.StartTime).ToList();
            for (var i = 1; i < ordered.Count; i++)
            {
                if (EndSpan(ordered[i - 1]) > ordered[i].StartTime.ToTimeSpan())
                    errors.Add($"{day.Key}: windows {Format(ordered[i - 1])} and {Format(ordered[i])} overlap.");
            }
        }

        if (errors.Count > 0)
            return (null, errors);

        return (new CapacityTemplate
        {
            Name = input.Name.Trim(),
            IsDefault = input.IsDefault,
            ValidFrom = input.IsDefault ? null : input.ValidFrom,
            ValidTo = input.IsDefault ? null : input.ValidTo,
            Priority = input.Priority,
            IsActive = true,
            Slots = slots,
        }, []);
    }

    // ------------------------------------------------------------ overrides

    public static (CapacityOverride? Override, IReadOnlyList<string> Errors) ValidateOverride(
        OverrideInput input, IReadOnlyDictionary<string, int> categoryIds, DateTimeOffset now)
    {
        var errors = new List<string>();

        if (input.EndAt <= input.StartAt)
            errors.Add("endAt must be after startAt.");
        else if (input.EndAt - input.StartAt > TimeSpan.FromDays(MaxOverrideDays))
            errors.Add($"An override can last at most {MaxOverrideDays} days; use a seasonal template for longer periods.");
        if (input.EndAt <= now)
            errors.Add("The override would end in the past.");

        if (string.IsNullOrWhiteSpace(input.Reason) || input.Reason.Trim().Length > 300)
            errors.Add("A reason is required (max 300 characters), e.g. 'Crane 3 maintenance'.");

        int? categoryId = null;
        if (!string.IsNullOrWhiteSpace(input.Category))
        {
            if (categoryIds.TryGetValue(input.Category.Trim().ToUpperInvariant(), out var id))
                categoryId = id;
            else
                errors.Add($"Unknown category '{input.Category}'.");
        }

        switch (input.Action)
        {
            case OverrideAction.Set when input.Value is null or < 0 or > MaxCapacityPerSlot:
                errors.Add($"SET needs a value of 0–{MaxCapacityPerSlot}.");
                break;
            case OverrideAction.Adjust when input.Value is null or 0 || Math.Abs(input.Value ?? 0) > MaxCapacityPerSlot:
                errors.Add("ADJUST needs a non-zero value (negative to reduce).");
                break;
            case OverrideAction.Close when input.Value is not null:
                errors.Add("CLOSE takes no value.");
                break;
        }
        if (!Enum.IsDefined(input.Action))
            errors.Add("Action must be SET, ADJUST or CLOSE.");

        if (errors.Count > 0)
            return (null, errors);

        return (new CapacityOverride
        {
            StartAt = input.StartAt.ToUniversalTime(),
            EndAt = input.EndAt.ToUniversalTime(),
            CategoryId = categoryId,
            Action = input.Action,
            Value = input.Action == OverrideAction.Close ? null : input.Value,
            Reason = input.Reason.Trim(),
            IsActive = true,
        }, []);
    }

    // ------------------------------------------------------------ reservations, release rules, settings

    public static (CapacityReservation? Reservation, IReadOnlyList<string> Errors) ValidateReservation(
        ReservationInput input, IReadOnlyDictionary<string, int> categoryIds)
    {
        var errors = new List<string>();
        if (input.EndAt <= input.StartAt)
            errors.Add("endAt must be after startAt.");
        if (input.UnitsPerSlot is < 1 or > MaxCapacityPerSlot)
            errors.Add($"unitsPerSlot must be 1–{MaxCapacityPerSlot}.");
        if (input.ReleaseHoursBefore is < 0 or > MaxReleaseHours)
            errors.Add($"releaseHoursBefore must be 0–{MaxReleaseHours}.");

        int? categoryId = null;
        if (!string.IsNullOrWhiteSpace(input.Category))
        {
            if (categoryIds.TryGetValue(input.Category.Trim().ToUpperInvariant(), out var id))
                categoryId = id;
            else
                errors.Add($"Unknown category '{input.Category}'.");
        }

        if (errors.Count > 0)
            return (null, errors);

        return (new CapacityReservation
        {
            CompanyId = input.CompanyId,
            CategoryId = categoryId,
            StartAt = input.StartAt.ToUniversalTime(),
            EndAt = input.EndAt.ToUniversalTime(),
            UnitsPerSlot = input.UnitsPerSlot,
            ReleaseHoursBefore = input.ReleaseHoursBefore,
            IsActive = true,
        }, []);
    }

    public static (IReadOnlyList<QuotaReleaseRule> Rules, IReadOnlyList<string> Errors) ValidateReleaseRules(
        IReadOnlyList<ReleaseRuleInput> input, IReadOnlyDictionary<string, int> categoryIds)
    {
        var errors = new List<string>();
        var rules = new List<QuotaReleaseRule>();
        var seen = new HashSet<int>();

        foreach (var rule in input ?? Array.Empty<ReleaseRuleInput>())
        {
            if (!categoryIds.TryGetValue((rule.Category ?? "").Trim().ToUpperInvariant(), out var categoryId))
                errors.Add($"Unknown category '{rule.Category}'.");
            else if (rule.HoursBefore is < 0 or > MaxReleaseHours)
                errors.Add($"{rule.Category}: hoursBefore must be 0–{MaxReleaseHours}.");
            else if (!seen.Add(categoryId))
                errors.Add($"{rule.Category} appears twice.");
            else
                rules.Add(new QuotaReleaseRule { CategoryId = categoryId, HoursBefore = rule.HoursBefore });
        }

        return errors.Count > 0 ? (Array.Empty<QuotaReleaseRule>(), errors) : (rules, Array.Empty<string>());
    }

    public static IReadOnlyList<string> ValidateSettings(IReadOnlyDictionary<string, int> settings)
    {
        var errors = new List<string>();
        foreach (var (key, value) in settings ?? new Dictionary<string, int>())
        {
            if (!SettingRanges.TryGetValue(key, out var range))
                errors.Add($"Unknown setting '{key}'. Allowed: {string.Join(", ", SettingRanges.Keys)}.");
            else if (value < range.Min || value > range.Max)
                errors.Add($"{key} must be {range.Min}–{range.Max}.");
        }
        return errors;
    }

    // ------------------------------------------------------------ views

    public static TemplateView ToView(CapacityTemplate t, IReadOnlyDictionary<int, string> categoryCodes) =>
        new(
            t.TemplateId, t.Name, t.IsDefault, t.ValidFrom, t.ValidTo, t.Priority, t.IsActive,
            t.Slots
                .OrderBy(s => s.Day).ThenBy(s => s.StartTime)
                .Select(s => new TemplateWindowView(
                    s.Day,
                    s.StartTime.ToString("HH:mm", CultureInfo.InvariantCulture),
                    s.EndTime >= EndOfDay ? "24:00" : s.EndTime.ToString("HH:mm", CultureInfo.InvariantCulture),
                    s.TotalCapacity,
                    s.OverbookPct,
                    s.Quotas
                        .Where(q => categoryCodes.ContainsKey(q.CategoryId))
                        .OrderBy(q => categoryCodes[q.CategoryId])
                        .ToDictionary(q => categoryCodes[q.CategoryId], q => q.MaxUnits)))
                .ToList());

    // ------------------------------------------------------------ helpers

    private static TimeOnly? ParseTime(string? value) =>
        TimeOnly.TryParseExact(value?.Trim(), ["HH:mm", "H:mm", "HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
            ? t
            : null;

    /// <summary>"24:00" (and "23:59") mean midnight, stored as 23:59.</summary>
    private static TimeOnly? ParseEnd(string? value)
    {
        if (value?.Trim() is "24:00" or "24:00:00")
            return EndOfDay;
        var time = ParseTime(value);
        return time is { } t && t >= EndOfDay ? EndOfDay : time;
    }

    private static TimeSpan EndSpan(CapacityTemplateSlot s) =>
        s.EndTime >= EndOfDay ? TimeSpan.FromDays(1) : s.EndTime.ToTimeSpan();

    private static string Format(CapacityTemplateSlot s) =>
        $"{s.StartTime:HH\\:mm}–{(s.EndTime >= EndOfDay ? "24:00" : s.EndTime.ToString("HH:mm", CultureInfo.InvariantCulture))}";
}
