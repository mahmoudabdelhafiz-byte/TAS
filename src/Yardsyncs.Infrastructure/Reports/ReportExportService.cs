using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Yardsyncs.Application.Reports;
using Yardsyncs.Domain;
using Yardsyncs.Domain.Booking;
using Yardsyncs.Domain.Gate;
using Yardsyncs.Infrastructure.Persistence;

namespace Yardsyncs.Infrastructure.Reports;

/// <summary>
/// Reports as Excel workbooks, headed in the reader's language (English or Arabic, Arabic sheets right to left):
/// the appointments of a period, the terminal's KPIs with every company, and one company's report. Times are the
/// terminal's local times; rates are numbers formatted as percentages, durations whole minutes.
/// </summary>
public sealed class ReportExportService(YardsyncsDbContext db, KpiService kpis, CompanyReportService companies)
{
    public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    /// <summary>The most appointments in one file (a quarter of a busy terminal).</summary>
    public const int MaxRows = 100_000;

    /// <exception cref="KeyNotFoundException">Unknown terminal.</exception>
    public async Task<byte[]> AppointmentsAsync(int terminalId, int? companyId, DateOnly from, DateOnly to, string language, CancellationToken ct = default)
    {
        var (timeZone, start, end) = await PeriodAsync(terminalId, from, to, ct);
        var walkIns = (await db.GateTroubles.AsNoTracking()
            .Where(t => t.TerminalId == terminalId && t.Resolution == TroubleResolution.WalkIn && t.AppointmentId != null)
            .Select(t => t.AppointmentId!.Value).ToListAsync(ct)).ToHashSet();
        var rows = await db.Appointments.AsNoTracking()
            .Where(a => a.TerminalId == terminalId && a.Slot.StartAt >= start && a.Slot.StartAt < end)
            .Where(a => companyId == null || a.CompanyId == companyId)
            .OrderBy(a => a.Slot.StartAt).ThenBy(a => a.AppointmentNo)
            .Take(MaxRows)
            .Select(a => new
            {
                a.AppointmentId, a.AppointmentNo, a.Status, a.Slot.StartAt, Company = a.Company.Name,
                Plate = a.Truck != null ? a.Truck.PlateNumber : null, Driver = a.Driver != null ? a.Driver.FullName : null,
                a.TrafficLight, a.ArrivedAt, a.GateInAt, a.GateOutAt,
                Items = a.Items.OrderBy(i => i.ItemId).Select(i => new { i.Category.Code, Ref = i.ContainerNo ?? i.BookingRef ?? i.EdoRef }).ToList(),
            })
            .ToListAsync(ct);

        var l = Labels.For(language);
        using var book = new XLWorkbook();
        Sheet(book, l, l["appointments"],
            ["appointment", "status", "slot", "company", "truck", "driver", "moves", "references", "ready", "arrived", "gateIn", "gateOut", "wait", "turn", "walkIn"],
            rows.Select(a => new object?[]
            {
                a.AppointmentNo, l[$"status.{a.Status}"], Local(a.StartAt, timeZone), a.Company, a.Plate, a.Driver,
                string.Join(", ", a.Items.Select(i => i.Code)), string.Join(", ", a.Items.Select(i => i.Ref).Where(r => r is not null)),
                l[$"light.{a.TrafficLight}"], Local(a.ArrivedAt, timeZone), Local(a.GateInAt, timeZone), Local(a.GateOutAt, timeZone),
                Minutes(a.ArrivedAt, a.GateInAt), Minutes(a.ArrivedAt ?? a.GateInAt, a.GateOutAt),
                walkIns.Contains(a.AppointmentId) ? l["yes"] : null,
            }));
        return Save(book);
    }

    /// <exception cref="KeyNotFoundException">Unknown terminal.</exception>
    public async Task<byte[]> KpisAsync(int terminalId, DateOnly from, DateOnly to, string language, CancellationToken ct = default)
    {
        var report = await kpis.ReportAsync(terminalId, from, to, ct);
        var rows = await companies.CompaniesAsync(terminalId, from, to, ct);
        var l = Labels.For(language);
        using var book = new XLWorkbook();
        Summary(book, l, from, to, report.Totals, withCapacity: true);
        Days(book, l, report.Days, withCapacity: true);
        Sheet(book, l, l["hours"], ["hour", "capacity", "booked", "slotUse", "appointments", "turn"],
            report.Hours.Select(h => new object?[] { $"{h.Hour:00}:00", h.Capacity, h.Booked, Rate(h.Utilization), h.Appointments, Round(h.AvgTurnMinutes) }));
        Rows(book, l, l["companies"], "company", rows);
        return Save(book);
    }

    /// <exception cref="KeyNotFoundException">Unknown terminal.</exception>
    public async Task<byte[]?> CompanyReportAsync(int terminalId, int companyId, DateOnly from, DateOnly to, string language, CancellationToken ct = default)
    {
        var report = await companies.ReportAsync(terminalId, companyId, from, to, ct);
        if (report is null)
            return null;
        var l = Labels.For(language);
        using var book = new XLWorkbook();
        Summary(book, l, from, to, report.Totals, withCapacity: false, company: report.CompanyName);
        Days(book, l, report.Days, withCapacity: false);
        Rows(book, l, l["trucks"], "truck", report.Trucks);
        Rows(book, l, l["drivers"], "driver", report.Drivers);
        Sheet(book, l, l["holds"], ["check", "visits", "lastReason"],
            report.Holds.Select(h => new object?[] { l[$"check.{h.Check}"], h.Visits, h.LastReason }));
        Sheet(book, l, l["charges"], ["currency", "charges", "amount", "noShowCharges", "lateCancels"],
            report.Charges.Select(c => new object?[] { c.Currency, c.Charges, c.AmountMinor / Math.Pow(10, Decimals(c.Currency)), c.NoShows, c.LateCancels }));
        return Save(book);
    }

    // ================================================================ sheets

    private static void Summary(XLWorkbook book, Labels l, DateOnly from, DateOnly to, KpiTotals t, bool withCapacity, string? company = null)
    {
        var lines = new List<object?[]>();
        if (company is not null)
            lines.Add([l["company"], company]);
        lines.Add([l["period"], $"{from:yyyy-MM-dd} – {to:yyyy-MM-dd}"]);
        if (withCapacity)
        {
            lines.Add([l["capacity"], t.Capacity]);
            lines.Add([l["booked"], t.Booked]);
            lines.Add([l["slotUse"], Rate(t.Utilization)]);
        }
        lines.Add([l["appointments"], t.Appointments]);
        lines.Add([l["completed"], t.Completed]);
        lines.Add([l["noShows"], t.NoShows]);
        lines.Add([l["cancelled"], t.Cancelled]);
        lines.Add([l["walkIns"], t.WalkIns]);
        lines.Add([l["noShowRate"], Rate(t.NoShowRate)]);
        lines.Add([l["onTime"], Rate(t.OnTimeRate)]);
        lines.Add([l["turn"], Round(t.AvgTurnMinutes)]);
        lines.Add([l["wait"], Round(t.AvgWaitMinutes)]);
        Sheet(book, l, l["summary"], ["item", "value"], lines);
    }

    private static void Days(XLWorkbook book, Labels l, IReadOnlyList<KpiDay> days, bool withCapacity)
    {
        var columns = withCapacity
            ? new[] { "day", "capacity", "booked", "slotUse", "appointments", "completed", "noShows", "cancelled", "walkIns", "onTime", "turn", "wait" }
            : new[] { "day", "appointments", "completed", "noShows", "cancelled", "walkIns", "onTime", "turn", "wait" };
        Sheet(book, l, l["days"], columns, days.Select<KpiDay, object?[]>(d =>
        {
            object?[] figures = [d.Appointments, d.Completed, d.NoShows, d.Cancelled, d.WalkIns,
                Rate(d.Arrived == 0 ? null : (double)d.OnTime / d.Arrived), Round(d.AvgTurnMinutes), Round(d.AvgWaitMinutes)];
            return withCapacity
                ? [d.Date.ToDateTime(TimeOnly.MinValue), d.Capacity, d.Booked, Rate(d.Utilization), .. figures]
                : [d.Date.ToDateTime(TimeOnly.MinValue), .. figures];
        }));
    }

    private static void Rows(XLWorkbook book, Labels l, string name, string first, IReadOnlyList<ReportRow> rows) =>
        Sheet(book, l, name, [first, "appointments", "completed", "noShows", "cancelled", "noShowRate", "onTime", "turn", "wait"],
            rows.Select(r => new object?[] { r.Name, r.Appointments, r.Completed, r.NoShows, r.Cancelled, Rate(r.NoShowRate), Rate(r.OnTimeRate), Round(r.AvgTurnMinutes), Round(r.AvgWaitMinutes) }));

    /// <summary>A sheet: bold header row (frozen), one row per item; dates, rates and minutes formatted.</summary>
    private static void Sheet(XLWorkbook book, Labels l, string name, IReadOnlyList<string> columns, IEnumerable<object?[]> rows)
    {
        var sheet = book.Worksheets.Add(name);
        sheet.RightToLeft = l.RightToLeft;
        for (var c = 0; c < columns.Count; c++)
        {
            var cell = sheet.Cell(1, c + 1);
            cell.SetValue(l[columns[c]]);
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#E3F1F2");
        }
        var r = 2;
        foreach (var row in rows)
        {
            for (var c = 0; c < row.Length; c++)
            {
                var cell = sheet.Cell(r, c + 1);
                switch (row[c])
                {
                    case null:
                        break;
                    case DateTime time: // local date and time, or a whole day
                        cell.SetValue(time);
                        cell.Style.DateFormat.Format = time.TimeOfDay == TimeSpan.Zero && columns[c] == "day" ? "yyyy-mm-dd" : "yyyy-mm-dd hh:mm";
                        break;
                    case RateValue rate:
                        cell.SetValue(rate.Value);
                        cell.Style.NumberFormat.Format = "0%";
                        break;
                    case int or long or double:
                        cell.SetValue(Convert.ToDouble(row[c], System.Globalization.CultureInfo.InvariantCulture));
                        break;
                    default:
                        cell.SetValue(row[c]!.ToString());
                        break;
                }
            }
            r++;
        }
        sheet.SheetView.FreezeRows(1);
        sheet.Columns().AdjustToContents(1, Math.Min(r, 200));
    }

    private static byte[] Save(XLWorkbook book)
    {
        if (book.Worksheets.Count == 0)
            book.Worksheets.Add("-");
        using var output = new MemoryStream();
        book.SaveAs(output);
        return output.ToArray();
    }

    // ================================================================ values

    private sealed record RateValue(double Value);

    private static object? Rate(double? value) => value is { } v ? new RateValue(v) : null;

    private static object? Round(double? minutes) => minutes is { } m ? Math.Round(m) : null;

    private static object? Minutes(DateTimeOffset? from, DateTimeOffset? to) =>
        from is { } a && to is { } b && b >= a ? Math.Round((b - a).TotalMinutes) : null;

    private static object? Local(DateTimeOffset? at, TimeZoneInfo timeZone) =>
        at is { } t ? TimeZoneInfo.ConvertTime(t, timeZone).DateTime : null;

    private static int Decimals(string currency) => currency.ToUpperInvariant() switch
    {
        "KWD" or "BHD" or "OMR" or "JOD" or "IQD" or "LYD" or "TND" => 3,
        "JPY" or "KRW" => 0,
        _ => 2,
    };

    private async Task<(TimeZoneInfo TimeZone, DateTimeOffset Start, DateTimeOffset End)> PeriodAsync(int terminalId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var zone = await db.Terminals.AsNoTracking().Where(t => t.TerminalId == terminalId).Select(t => t.TimeZone).SingleOrDefaultAsync(ct)
                   ?? throw new KeyNotFoundException($"Terminal {terminalId} not found.");
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(zone);
        DateTimeOffset StartOf(DateOnly d)
        {
            var local = d.ToDateTime(TimeOnly.MinValue);
            return new DateTimeOffset(local, timeZone.GetUtcOffset(local)).ToUniversalTime();
        }
        return (timeZone, StartOf(from), StartOf(to.AddDays(1)));
    }

    /// <summary>Column and value names in English or Arabic.</summary>
    private sealed class Labels(IReadOnlyDictionary<string, string> texts, bool rightToLeft)
    {
        public bool RightToLeft { get; } = rightToLeft;

        public string this[string key] => texts.TryGetValue(key, out var text) ? text : En.GetValueOrDefault(key, key);

        public static Labels For(string? language) =>
            string.Equals(language, "ar", StringComparison.OrdinalIgnoreCase) ? new Labels(Ar, true) : new Labels(En, false);

        private static readonly Dictionary<string, string> En = new()
        {
            ["appointments"] = "Appointments", ["appointment"] = "Appointment", ["status"] = "Status", ["slot"] = "Slot", ["company"] = "Company",
            ["companies"] = "Companies", ["truck"] = "Truck", ["trucks"] = "Trucks", ["driver"] = "Driver", ["drivers"] = "Drivers",
            ["moves"] = "Moves", ["references"] = "Containers / references", ["ready"] = "Ready to go", ["arrived"] = "Arrived",
            ["gateIn"] = "Gate in", ["gateOut"] = "Gate out", ["wait"] = "Waiting at the gate (min)", ["turn"] = "Turn time (min)",
            ["walkIn"] = "Walk-in", ["yes"] = "Yes", ["summary"] = "Summary", ["item"] = "Item", ["value"] = "Value", ["period"] = "Period",
            ["capacity"] = "Capacity", ["booked"] = "Booked", ["slotUse"] = "Slot use", ["completed"] = "Completed", ["noShows"] = "No-shows",
            ["cancelled"] = "Cancelled", ["walkIns"] = "Walk-ins", ["noShowRate"] = "No-show rate", ["onTime"] = "On time",
            ["days"] = "Days", ["day"] = "Day", ["hours"] = "Hours", ["hour"] = "Hour", ["holds"] = "Holds", ["check"] = "Check",
            ["visits"] = "Visits", ["lastReason"] = "Latest reason", ["charges"] = "Charges", ["currency"] = "Currency", ["amount"] = "Amount",
            ["noShowCharges"] = "For no-shows", ["lateCancels"] = "For late cancellations",
            ["status.Draft"] = "Draft", ["status.Confirmed"] = "Booked", ["status.EnRoute"] = "On the way", ["status.Arrived"] = "At the gate",
            ["status.GateIn"] = "In the terminal", ["status.Completed"] = "Completed", ["status.Cancelled"] = "Cancelled",
            ["status.NoShow"] = "No-show", ["status.RejectedAtGate"] = "Turned away",
            ["light.Green"] = "Ready", ["light.Amber"] = "Check", ["light.Red"] = "On hold", ["light.Grey"] = "Not yet",
            ["check.ContainerNumber"] = "Container number", ["check.Availability"] = "Not in the yard", ["check.CustomsRelease"] = "Customs",
            ["check.LineRelease"] = "Line release", ["check.TerminalHold"] = "Terminal hold", ["check.Payment"] = "Unpaid charges",
            ["check.BookingValid"] = "Booking", ["check.Vgm"] = "VGM", ["check.Cutoff"] = "Cut-off", ["check.CargoRelease"] = "Cargo release",
        };

        private static readonly Dictionary<string, string> Ar = new()
        {
            ["appointments"] = "المواعيد", ["appointment"] = "الموعد", ["status"] = "الحالة", ["slot"] = "الفترة", ["company"] = "الشركة",
            ["companies"] = "الشركات", ["truck"] = "الشاحنة", ["trucks"] = "الشاحنات", ["driver"] = "السائق", ["drivers"] = "السائقون",
            ["moves"] = "العمليات", ["references"] = "الحاويات / المراجع", ["ready"] = "جاهزية الانطلاق", ["arrived"] = "الوصول",
            ["gateIn"] = "الدخول", ["gateOut"] = "الخروج", ["wait"] = "الانتظار عند البوابة (دقيقة)", ["turn"] = "زمن الدورة (دقيقة)",
            ["walkIn"] = "بدون موعد", ["yes"] = "نعم", ["summary"] = "الملخص", ["item"] = "البند", ["value"] = "القيمة", ["period"] = "الفترة",
            ["capacity"] = "السعة", ["booked"] = "المحجوز", ["slotUse"] = "استخدام الفترات", ["completed"] = "المكتملة", ["noShows"] = "عدم الحضور",
            ["cancelled"] = "الملغاة", ["walkIns"] = "بدون موعد", ["noShowRate"] = "نسبة عدم الحضور", ["onTime"] = "في الموعد",
            ["days"] = "الأيام", ["day"] = "اليوم", ["hours"] = "الساعات", ["hour"] = "الساعة", ["holds"] = "حالات الإيقاف", ["check"] = "التحقق",
            ["visits"] = "الزيارات", ["lastReason"] = "آخر سبب", ["charges"] = "الرسوم", ["currency"] = "العملة", ["amount"] = "المبلغ",
            ["noShowCharges"] = "لعدم الحضور", ["lateCancels"] = "للإلغاء المتأخر",
            ["status.Draft"] = "مسودة", ["status.Confirmed"] = "محجوز", ["status.EnRoute"] = "في الطريق", ["status.Arrived"] = "عند البوابة",
            ["status.GateIn"] = "داخل المحطة", ["status.Completed"] = "مكتمل", ["status.Cancelled"] = "ملغى", ["status.NoShow"] = "لم يحضر",
            ["status.RejectedAtGate"] = "رُدّ من البوابة",
            ["light.Green"] = "جاهز", ["light.Amber"] = "تحقق", ["light.Red"] = "موقوف", ["light.Grey"] = "ليس بعد",
            ["check.ContainerNumber"] = "رقم الحاوية", ["check.Availability"] = "غير موجودة في الساحة", ["check.CustomsRelease"] = "الجمارك",
            ["check.LineRelease"] = "إفراج الخط الملاحي", ["check.TerminalHold"] = "إيقاف المحطة", ["check.Payment"] = "رسوم غير مدفوعة",
            ["check.BookingValid"] = "الحجز", ["check.Vgm"] = "الوزن المتحقق (VGM)", ["check.Cutoff"] = "موعد الإغلاق", ["check.CargoRelease"] = "إفراج البضاعة",
        };
    }
}
