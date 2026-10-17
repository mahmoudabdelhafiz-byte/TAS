using System.Globalization;
using System.Text;

namespace Yardsyncs.Application.Reports;

/// <summary>
/// The emailed reports as plain text in English or Arabic: the daily one (yesterday's figures and today's bookings) and
/// the weekly one (the seven days before, and the companies with the most no-shows). The figures also go as Excel.
/// </summary>
public static class ReportDigest
{
    public static (string Subject, string Body) Daily(string language, string terminal, KpiReport yesterday, KpiReport today)
    {
        var ar = IsArabic(language);
        var y = yesterday.Totals;
        var t = today.Totals;
        var body = new StringBuilder();
        body.AppendLine(ar ? $"تقرير {terminal} اليومي" : $"{terminal} — daily report").AppendLine();
        body.AppendLine(ar ? $"أمس ({Day(yesterday.From, ar)}):" : $"Yesterday ({Day(yesterday.From, ar)}):");
        Figures(body, y, ar);
        body.AppendLine();
        body.AppendLine(ar ? $"اليوم ({Day(today.From, ar)}):" : $"Today ({Day(today.From, ar)}):");
        body.AppendLine(ar
            ? $"- محجوز: {t.Booked} من {t.Capacity} ({Pct(t.Utilization)})"
            : $"- Booked: {t.Booked} of {t.Capacity} ({Pct(t.Utilization)})");
        if (today.Hours.Where(h => h.Capacity > 0).OrderByDescending(h => h.Utilization ?? 0).FirstOrDefault() is { Utilization: > 0 } busiest)
            body.AppendLine(ar
                ? $"- أكثر ساعة ازدحاماً: {busiest.Hour:00}:00 ({Pct(busiest.Utilization)})"
                : $"- Busiest hour: {busiest.Hour:00}:00 ({Pct(busiest.Utilization)})");
        body.AppendLine().AppendLine(ar ? "التفاصيل في ملف Excel المرفق." : "The details are in the attached Excel file.");
        return (ar ? $"تقرير يوم {Day(yesterday.From, ar)}" : $"Daily report {Day(yesterday.From, ar)}", body.ToString());
    }

    public static (string Subject, string Body) Weekly(string language, string terminal, KpiReport week, IReadOnlyList<ReportRow> companies)
    {
        var ar = IsArabic(language);
        var body = new StringBuilder();
        var period = $"{Day(week.From, ar)} – {Day(week.To, ar)}";
        body.AppendLine(ar ? $"تقرير {terminal} الأسبوعي، {period}" : $"{terminal} — weekly report, {period}").AppendLine();
        body.AppendLine(ar
            ? $"- استخدام الفترات: {Pct(week.Totals.Utilization)} ({week.Totals.Booked} من {week.Totals.Capacity})"
            : $"- Slot use: {Pct(week.Totals.Utilization)} ({week.Totals.Booked} of {week.Totals.Capacity})");
        Figures(body, week.Totals, ar);
        var worst = companies.Where(c => c.NoShows > 0).Take(3).ToList();
        if (worst.Count > 0)
        {
            body.AppendLine().AppendLine(ar ? "الأكثر في عدم الحضور:" : "Most no-shows:");
            foreach (var c in worst)
                body.AppendLine(ar
                    ? $"- {c.Name}: {c.NoShows} ({Pct(c.NoShowRate)}) من {c.Appointments} موعد"
                    : $"- {c.Name}: {c.NoShows} ({Pct(c.NoShowRate)}) of {c.Appointments} appointments");
        }
        body.AppendLine().AppendLine(ar ? "التفاصيل في ملف Excel المرفق." : "The details are in the attached Excel file.");
        return (ar ? $"التقرير الأسبوعي {period}" : $"Weekly report {period}", body.ToString());
    }

    private static void Figures(StringBuilder body, KpiTotals t, bool ar)
    {
        body.AppendLine(ar
            ? $"- المواعيد: {t.Appointments} (مكتملة {t.Completed}، ملغاة {t.Cancelled})"
            : $"- Appointments: {t.Appointments} ({t.Completed} completed, {t.Cancelled} cancelled)");
        body.AppendLine(ar ? $"- عدم الحضور: {t.NoShows} ({Pct(t.NoShowRate)})" : $"- No-shows: {t.NoShows} ({Pct(t.NoShowRate)})");
        body.AppendLine(ar ? $"- في الموعد: {Pct(t.OnTimeRate)}" : $"- On time: {Pct(t.OnTimeRate)}");
        body.AppendLine(ar
            ? $"- زمن الدورة: {Minutes(t.AvgTurnMinutes, ar)}، الانتظار عند البوابة: {Minutes(t.AvgWaitMinutes, ar)}"
            : $"- Turn time: {Minutes(t.AvgTurnMinutes, ar)}, waiting at the gate: {Minutes(t.AvgWaitMinutes, ar)}");
        if (t.WalkIns > 0)
            body.AppendLine(ar ? $"- بدون موعد: {t.WalkIns}" : $"- Walk-ins: {t.WalkIns}");
    }

    private static bool IsArabic(string language) => string.Equals(language?.Trim(), "ar", StringComparison.OrdinalIgnoreCase);

    private static string Pct(double? value) => value is { } v ? $"{Math.Round(v * 100).ToString(CultureInfo.InvariantCulture)}%" : "—";

    private static string Minutes(double? value, bool ar) =>
        value is { } v ? $"{Math.Round(v).ToString(CultureInfo.InvariantCulture)} {(ar ? "دقيقة" : "min")}" : "—";

    private static string Day(DateOnly date, bool ar) =>
        ar ? Languages.ServerText.Dates(date.ToString("ddd dd MMM", CultureInfo.InvariantCulture), "ar") : date.ToString("ddd dd MMM", CultureInfo.InvariantCulture);
}
