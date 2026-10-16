namespace Yardsyncs.Domain;

/// <summary>Key/value rule for a terminal, e.g. early_grace_min = 30.</summary>
public sealed class TerminalSetting
{
    public int TerminalId { get; set; }
    public string SettingKey { get; set; } = "";
    public string SettingValue { get; set; } = "";
}

/// <summary>How strictly the gate identifies the driver (setting driver_identity).</summary>
public enum DriverIdentity
{
    /// <summary>Not checked: whoever drives the booked truck.</summary>
    NotChecked = 0,

    /// <summary>The appointment must name its driver (blocked drivers are refused).</summary>
    NamedDriver = 1,

    /// <summary>The named driver shows the gate pass from Yardsyncs mobile, signed in on their own phone.</summary>
    DriverGatePass = 2,
}

/// <summary>Typed view of a terminal's settings with defaults for anything not configured.</summary>
/// <param name="GateByNumber">The gate may find the appointment by its typed number (the QR gate pass always works).</param>
/// <param name="GateByPlate">… by the truck's plate (OCR or typed).</param>
/// <param name="GateByContainer">… by a container number (OCR or typed).</param>
public sealed record TerminalSettings(
    int EarlyGraceMinutes,
    int LateGraceMinutes,
    int CancelCutoffMinutes,
    int MaxAppointmentsPerTruckPerDay,
    bool GateByNumber = true,
    bool GateByPlate = true,
    bool GateByContainer = true,
    DriverIdentity DriverIdentity = DriverIdentity.NotChecked,
    long FeeBooking = 0,
    long FeeNoShow = 0,
    long FeeLateCancel = 0,
    int LateCancelHours = 0)
{
    /// <summary>Fees, in the currency's smallest unit (halalas for SAR); 0 = no fee.</summary>
    public const string FeeBookingKey = "fee_booking";
    public const string FeeNoShowKey = "fee_no_show";
    public const string FeeLateCancelKey = "fee_late_cancel";

    /// <summary>A company cancelling less than this many hours before the slot pays the late-cancellation fee.</summary>
    public const string LateCancelHoursKey = "late_cancel_hours";

    public const string EarlyGraceKey = "early_grace_min";
    public const string LateGraceKey = "late_grace_min";
    public const string CancelCutoffKey = "cancel_cutoff_min";
    public const string MaxPerTruckPerDayKey = "max_appt_per_truck_per_day";
    public const string GateByNumberKey = "gate_by_number";
    public const string GateByPlateKey = "gate_by_plate";
    public const string GateByContainerKey = "gate_by_container";
    public const string DriverIdentityKey = "driver_identity";

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
            Get(MaxPerTruckPerDayKey, Defaults.MaxAppointmentsPerTruckPerDay),
            Get(GateByNumberKey, 1) != 0,
            Get(GateByPlateKey, 1) != 0,
            Get(GateByContainerKey, 1) != 0,
            (DriverIdentity)Math.Min(Get(DriverIdentityKey, 0), 2),
            Get(FeeBookingKey, 0),
            Get(FeeNoShowKey, 0),
            Get(FeeLateCancelKey, 0),
            Get(LateCancelHoursKey, 0));
    }
}
