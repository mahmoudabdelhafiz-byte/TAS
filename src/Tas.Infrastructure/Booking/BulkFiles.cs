using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using Tas.Application.Booking.Bulk;

namespace Tas.Infrastructure.Booking;

/// <summary>Reads bulk uploads (.xlsx, .csv) into a plain text table, and builds the downloadable templates.</summary>
public static class BulkFiles
{
    public const long MaxUploadBytes = 2 * 1024 * 1024;

    /// <summary>First worksheet as text. Dates become yyyy-MM-dd and times HH:mm whatever Excel turned them into.</summary>
    public static IReadOnlyList<IReadOnlyList<string>> ReadExcel(Stream stream)
    {
        using var workbook = new XLWorkbook(stream);
        var sheet = workbook.Worksheets.First();
        var used = sheet.RangeUsed();
        if (used is null)
            return [];

        var table = new List<IReadOnlyList<string>>();
        var lastColumn = used.LastColumn().ColumnNumber();
        foreach (var row in used.Rows())
        {
            var cells = new List<string>();
            for (var col = 1; col <= lastColumn; col++)
                cells.Add(CellText(sheet.Cell(row.RowNumber(), col)));
            table.Add(cells);
        }
        return table;
    }

    public static IReadOnlyList<IReadOnlyList<string>> ReadCsv(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return CsvTable.Parse(reader.ReadToEnd());
    }

    private static string CellText(IXLCell cell)
    {
        switch (cell.DataType)
        {
            case XLDataType.Blank:
                return "";
            case XLDataType.DateTime:
                var dt = cell.GetDateTime();
                if (dt.Year < 1900) // Excel stores a time-only value on 1899-12-30
                    return dt.ToString("HH:mm", CultureInfo.InvariantCulture);
                return dt.TimeOfDay == TimeSpan.Zero
                    ? dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    : dt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            case XLDataType.TimeSpan:
                var ts = cell.GetTimeSpan();
                return $"{(int)ts.TotalHours:00}:{ts.Minutes:00}";
            case XLDataType.Number:
                return cell.GetDouble().ToString(CultureInfo.InvariantCulture);
            default:
                return cell.GetFormattedString();
        }
    }

    // ------------------------------------------------------------------ templates

    private static IReadOnlyList<string[]> Examples(DateOnly date)
    {
        var d = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return
        [
            ["V1", d, "10:00", "EXP_DROP", "HLXU8700018", "45G1", "BKG1001", "", "", "", "1234 ABJ", "1012345678"],
            ["V1", d, "10:00", "IMP_PICKUP", "MSCU1234566", "", "", "EDO-5501", "", "", "1234 ABJ", "1012345678"],
            ["", d, "11:00", "MTY_RETURN", "MAEU4001110", "", "", "", "", "", "5678 HDR", "2012345678"],
            ["", d, "12:00", "BB_DELIVERY", "", "", "", "", "BL-STEEL-001", "28", "", ""],
        ];
    }

    private static readonly string[] Instructions =
    [
        "One row per move. Required columns: date, time, category.",
        "date: the slot day (yyyy-MM-dd or dd/MM/yyyy). time: the slot start (HH:mm), local terminal time.",
        "category: the transaction code, e.g. IMP_PICKUP, EXP_DROP, MTY_RETURN, MTY_PICKUP, BB_DELIVERY.",
        "Container moves: container_no (or booking_ref alone for an empty pickup). Export drops: booking_ref. Import pickups: edo_ref if you have one.",
        "Breakbulk / bulk: cargo_ref (BL / DO) instead of a container; planned_tonnage optional.",
        "Dual transaction: give the rows the same 'visit' value (same date, time, truck and driver). Up to 4 moves per visit.",
        "truck_plate and driver_id (ID / Iqama) are optional; they must be registered for your company. Arabic plates are accepted.",
        "Upload with dryRun=true first to see what would be booked and why anything would fail. Nothing is booked in a dry run.",
        "Each visit is booked on its own: rows that fail do not stop the others. At most 500 rows per file.",
    ];

    public static byte[] CsvTemplate(DateOnly exampleDate)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", BulkSheet.Columns));
        foreach (var row in Examples(exampleDate))
            sb.AppendLine(string.Join(",", row.Select(v => v.Contains(',') ? $"\"{v}\"" : v)));
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];
    }

    public static byte[] ExcelTemplate(DateOnly exampleDate)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Appointments");

        // Text format everywhere so Excel doesn't turn dates, times and container numbers into numbers.
        sheet.Columns(1, BulkSheet.Columns.Count).Style.NumberFormat.Format = "@";

        for (var c = 0; c < BulkSheet.Columns.Count; c++)
        {
            var header = sheet.Cell(1, c + 1);
            header.SetValue(BulkSheet.Columns[c]);
            header.Style.Font.Bold = true;
            header.Style.Fill.BackgroundColor = XLColor.FromHtml("#DCE6F1");
        }

        var r = 2;
        foreach (var row in Examples(exampleDate))
        {
            for (var c = 0; c < row.Length; c++)
                sheet.Cell(r, c + 1).SetValue(row[c]);
            r++;
        }
        sheet.SheetView.FreezeRows(1);
        sheet.Columns().AdjustToContents();

        var help = workbook.Worksheets.Add("Instructions");
        for (var i = 0; i < Instructions.Length; i++)
            help.Cell(i + 1, 1).SetValue(Instructions[i]);
        help.Column(1).Width = 140;

        using var output = new MemoryStream();
        workbook.SaveAs(output);
        return output.ToArray();
    }
}
