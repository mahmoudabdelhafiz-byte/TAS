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
    int LateCancelHours = 0,
    bool TosGateWrite = false,
    bool Autogate = true,
    bool SealHold = false,
    int ReportHour = 7,
    int ReportWeekday = 0,
    int DriverPointsDays = 180,
    int DriverWarnPoints = 6,
    int DriverLimitPoints = 0,
    bool DriverRecordToCompany = true,
    int InductionMode = 0,
    int SlotAdvice = 1,
    int SlotTargetWaitMinutes = 20,
    int SlotMaxOverbookPct = 15,
    int SlotHistoryWeeks = 8,
    bool SlotGateSpeed = true,
    int RoroMaxPerTruck = 8)
{
    /// <summary>Suggestions for the planner from the terminal's history: 0 = off, 1 = suggest (the planner approves each).</summary>
    public const string SlotAdviceKey = "slot_advice";

    /// <summary>Minutes from arrival to gate-in the terminal aims for (5–240); longer waits suggest fewer trucks per slot.</summary>
    public const string SlotTargetWaitKey = "slot_target_wait";

    /// <summary>The highest overbooking a suggestion may propose, in percent (0–50).</summary>
    public const string SlotMaxOverbookKey = "slot_max_overbook";

    /// <summary>Weeks of history the suggestions read (4–26).</summary>
    public const string SlotHistoryWeeksKey = "slot_history_weeks";

    /// <summary>Booking shows how quickly each hour usually gets through the gate (fast / busy): 1 = on, 0 = off.</summary>
    public const string SlotGateSpeedKey = "slot_gate_speed";

    /// <summary>RoRo: vehicles one car carrier may take on a visit (1–20).</summary>
    public const string RoroMaxPerTruckKey = "roro_max_per_truck";

    /// <summary>Drivers without a valid safety induction: 0 = nothing, 1 = the gate warns, 2 = the trouble desk.</summary>
    public const string InductionModeKey = "induction_mode";

    /// <summary>Days a driver's penalty points count (1–1825).</summary>
    public const string DriverPointsDaysKey = "driver_points_days";

    /// <summary>From this many points the gate, the trouble desk and the control room see a warning (0 = off).</summary>
    public const string DriverWarnPointsKey = "driver_warn_points";

    /// <summary>From this many points the driver is sent to the trouble desk at the gate (0 = off).</summary>
    public const string DriverLimitPointsKey = "driver_limit_points";

    /// <summary>Trucking companies see their own drivers' records (1) or not (0).</summary>
    public const string DriverRecordToCompanyKey = "driver_record_company";

    /// <summary>Local hour the emailed reports go out (0–23); the daily one covers yesterday.</summary>
    public const string ReportHourKey = "report_hour";

    /// <summary>Day of the week of the weekly report, 0 = Sunday … 6 = Saturday; it covers the seven days before.</summary>
    public const string ReportWeekdayKey = "report_weekday";

    /// <summary>
    /// Hold a truck at the entry when a seal read at the gate isn't the one the company declared: the trouble desk
    /// decides (SEAL_MISMATCH). Off: the mismatch is only recorded in the inspection.
    /// </summary>
    public const string SealHoldKey = "seal_hold";

    /// <summary>The terminal has the Autogate module (when the installation has it). Set by system admins: it is what the customer bought.</summary>
    public const string ModuleAutogateKey = "module_autogate";

    /// <summary>Record each gate-in and gate-out in the TOS (truck visit and gate transactions); off until the TOS link is tested.</summary>
    public const string TosGateWriteKey = "tos_gate_write";

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
            Get(LateCancelHoursKey, 0),
            Get(TosGateWriteKey, 0) != 0,
            Get(ModuleAutogateKey, 1) != 0,
            Get(SealHoldKey, 0) != 0,
            Math.Min(Get(ReportHourKey, 7), 23),
            Math.Min(Get(ReportWeekdayKey, 0), 6),
            Math.Clamp(Get(DriverPointsDaysKey, 180), 1, 1825),
            Get(DriverWarnPointsKey, 6),
            Get(DriverLimitPointsKey, 0),
            Get(DriverRecordToCompanyKey, 1) != 0,
            Math.Min(Get(InductionModeKey, 0), 2),
            Math.Min(Get(SlotAdviceKey, 1), 1),
            Math.Clamp(Get(SlotTargetWaitKey, 20), 5, 240),
            Math.Min(Get(SlotMaxOverbookKey, 15), 50),
            Math.Clamp(Get(SlotHistoryWeeksKey, 8), 4, 26),
            Get(SlotGateSpeedKey, 1) != 0,
            Math.Clamp(Get(RoroMaxPerTruckKey, 8), 1, 20));
    }
}
