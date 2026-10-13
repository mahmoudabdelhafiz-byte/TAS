namespace Tas.Domain;

/// <summary>Key/value rule for a terminal, e.g. early_grace_min = 30.</summary>
public sealed class TerminalSetting
{
    public int TerminalId { get; set; }
    public string SettingKey { get; set; } = "";
    public string SettingValue { get; set; } = "";
}

/// <summary>Typed view of a terminal's settings with defaults for anything not configured.</summary>
public sealed record TerminalSettings(
    int EarlyGraceMinutes,
    int LateGraceMinutes,
    int CancelCutoffMinutes,
    int MaxAppointmentsPerTruckPerDay)
{
    public const string EarlyGraceKey = "early_grace_min";
    public const string LateGraceKey = "late_grace_min";
    public const string CancelCutoffKey = "cancel_cutoff_min";
    public const string MaxPerTruckPerDayKey = "max_appt_per_truck_per_day";

    public static TerminalSettings Defaults { get; } = new(30, 30, 60, 0);

    public static TerminalSettings From(IEnumerable<TerminalSetting> settings)
    {
        var map = settings.ToDictionary(s => s.SettingKey, s => s.SettingValue, StringComparer.OrdinalIgnoreCase);

        int Get(string key, int fallback) =>
            map.TryGetValue(key, out var value) && int.TryParse(value, out var parsed) && parsed >= 0 ? parsed : fallback;

        return new TerminalSettings(
            Get(EarlyGraceKey, Defaults.EarlyGraceMinutes),
            Get(LateGraceKey, Defaults.LateGraceMinutes),
            Get(CancelCutoffKey, Defaults.CancelCutoffMinutes),
            Get(MaxPerTruckPerDayKey, Defaults.MaxAppointmentsPerTruckPerDay));
    }
}
