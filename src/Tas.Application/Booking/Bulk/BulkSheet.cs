using System.Globalization;
using Tas.Application.MasterData;

namespace Tas.Application.Booking.Bulk;

/// <summary>One data row of an upload. <see cref="RowNumber"/> is the row as the user sees it in the sheet (header = 1).</summary>
public sealed record BulkRow(
    int RowNumber,
    string? Visit,
    string? Date,
    string? Time,
    string? Category,
    string? ContainerNo,
    string? IsoType,
    string? BookingRef,
    string? EdoRef,
    string? CargoRef,
    decimal? PlannedTonnage,
    string? TruckPlate,
    string? DriverId);

/// <summary>Rows that make up one truck visit (one appointment).</summary>
public sealed record BulkVisit(string Key, IReadOnlyList<BulkRow> Rows, DateOnly Date, TimeOnly Time, string? TruckPlate, string? DriverId);

public sealed record RowError(int Row, string Message);

/// <summary>
/// Reads an upload laid out as a table with a header row. Columns (any order, case-insensitive):
///   visit · date · time · category · container_no · iso_type · booking_ref · edo_ref · cargo_ref · planned_tonnage · truck_plate · driver_id
/// Required: date, time, category. Rows sharing a <c>visit</c> value become one appointment (e.g. a dual
/// transaction); rows without one are each their own visit.
/// </summary>
public static class BulkSheet
{
    public const int MaxRows = 500;

    public static readonly IReadOnlyList<string> Columns =
        ["visit", "date", "time", "category", "container_no", "iso_type", "booking_ref", "edo_ref", "cargo_ref", "planned_tonnage", "truck_plate", "driver_id"];

    private static readonly string[] Required = ["date", "time", "category"];

    private static readonly Dictionary<string, string> Aliases = new()
    {
        ["group"] = "visit", ["visit_no"] = "visit", ["trip"] = "visit",
        ["slot_date"] = "date", ["day"] = "date",
        ["slot_time"] = "time", ["slot"] = "time", ["hour"] = "time",
        ["type"] = "category", ["transaction"] = "category",
        ["container"] = "container_no", ["containerno"] = "container_no", ["container_number"] = "container_no",
        ["iso"] = "iso_type", ["size_type"] = "iso_type",
        ["booking"] = "booking_ref", ["booking_no"] = "booking_ref", ["release"] = "booking_ref",
        ["edo"] = "edo_ref", ["delivery_order"] = "edo_ref",
        ["cargo"] = "cargo_ref", ["bl"] = "cargo_ref", ["bl_no"] = "cargo_ref",
        ["tonnage"] = "planned_tonnage", ["tons"] = "planned_tonnage",
        ["truck"] = "truck_plate", ["plate"] = "truck_plate", ["plate_no"] = "truck_plate",
        ["driver"] = "driver_id", ["driver_id_number"] = "driver_id", ["iqama"] = "driver_id", ["id_number"] = "driver_id",
    };

    private static readonly string[] DateFormats = ["yyyy-MM-dd", "yyyy/MM/dd", "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "d-M-yyyy", "dd.MM.yyyy"];
    private static readonly string[] TimeFormats = ["HH:mm", "H:mm", "HH:mm:ss", "H:mm:ss", "HHmm"];

    public static (IReadOnlyList<BulkRow> Rows, IReadOnlyList<RowError> Errors) Parse(IReadOnlyList<IReadOnlyList<string>> table)
    {
        var errors = new List<RowError>();
        var headerIndex = -1;
        for (var i = 0; i < table.Count; i++)
        {
            if (!IsBlank(table[i]))
            {
                headerIndex = i;
                break;
            }
        }
        if (headerIndex < 0)
            return (Array.Empty<BulkRow>(), new[] { new RowError(1, "The file is empty.") });

        // Map columns.
        var columns = new Dictionary<string, int>();
        var header = table[headerIndex];
        for (var c = 0; c < header.Count; c++)
        {
            var name = NormalizeHeader(header[c]);
            if (name.Length == 0)
                continue;
            var canonical = Columns.Contains(name) ? name : Aliases.GetValueOrDefault(name);
            if (canonical is null)
                errors.Add(new RowError(headerIndex + 1, $"Unknown column '{header[c].Trim()}'. Expected: {string.Join(", ", Columns)}."));
            else if (!columns.TryAdd(canonical, c))
                errors.Add(new RowError(headerIndex + 1, $"Column '{canonical}' appears twice."));
        }
        foreach (var required in Required.Where(r => !columns.ContainsKey(r)))
            errors.Add(new RowError(headerIndex + 1, $"Missing required column '{required}'."));
        if (errors.Count > 0)
            return (Array.Empty<BulkRow>(), errors);

        string? Cell(IReadOnlyList<string> row, string column) =>
            columns.TryGetValue(column, out var index) && index < row.Count && !string.IsNullOrWhiteSpace(row[index])
                ? row[index].Trim()
                : null;

        var rows = new List<BulkRow>();
        for (var i = headerIndex + 1; i < table.Count; i++)
        {
            var line = table[i];
            if (IsBlank(line))
                continue;

            var rowNumber = i + 1;
            if (rows.Count >= MaxRows)
            {
                errors.Add(new RowError(rowNumber, $"At most {MaxRows} rows per upload; split the file."));
                break;
            }

            decimal? tonnage = null;
            var tonnageText = Cell(line, "planned_tonnage");
            if (tonnageText is not null)
            {
                if (decimal.TryParse(tonnageText.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var t))
                    tonnage = t;
                else
                    errors.Add(new RowError(rowNumber, $"planned_tonnage '{tonnageText}' is not a number."));
            }

            rows.Add(new BulkRow(
                rowNumber,
                Cell(line, "visit"),
                Cell(line, "date"),
                Cell(line, "time"),
                Cell(line, "category"),
                Cell(line, "container_no"),
                Cell(line, "iso_type"),
                Cell(line, "booking_ref"),
                Cell(line, "edo_ref"),
                Cell(line, "cargo_ref"),
                tonnage,
                Cell(line, "truck_plate"),
                Cell(line, "driver_id")));
        }

        if (rows.Count == 0 && errors.Count == 0)
            errors.Add(new RowError(headerIndex + 2, "No data rows below the header."));

        return (rows, errors);
    }

    /// <summary>
    /// Groups rows into visits and checks what can be checked without the database: dates, times,
    /// consistency inside a visit, at most 4 moves per visit, no container twice in the file.
    /// Rows with errors are left out of the visits.
    /// </summary>
    public static (IReadOnlyList<BulkVisit> Visits, IReadOnlyList<RowError> Errors) Group(IReadOnlyList<BulkRow> rows)
    {
        var errors = new List<RowError>();
        var parsed = new List<(BulkRow Row, DateOnly Date, TimeOnly Time)>();
        var containers = new Dictionary<string, int>();

        foreach (var row in rows)
        {
            var rowErrors = new List<string>();
            if (!TryParseDate(row.Date, out var date))
                rowErrors.Add($"date '{row.Date}' is not a date (use yyyy-MM-dd or dd/MM/yyyy).");
            if (!TryParseTime(row.Time, out var time))
                rowErrors.Add($"time '{row.Time}' is not a time (use HH:mm, the slot start).");
            if (string.IsNullOrWhiteSpace(row.Category))
                rowErrors.Add("category is required.");

            if (row.ContainerNo is not null)
            {
                var container = ContainerNumber.Normalize(row.ContainerNo);
                if (containers.TryGetValue(container, out var firstRow))
                    rowErrors.Add($"container {container} is already on row {firstRow}.");
                else
                    containers[container] = row.RowNumber;
            }

            if (rowErrors.Count > 0)
                errors.AddRange(rowErrors.Select(e => new RowError(row.RowNumber, e)));
            else
                parsed.Add((row, date, time));
        }

        var visits = new List<BulkVisit>();
        foreach (var group in parsed.GroupBy(p => p.Row.Visit is null ? $"row-{p.Row.RowNumber}" : p.Row.Visit.Trim().ToUpperInvariant()))
        {
            var items = group.OrderBy(p => p.Row.RowNumber).ToList();
            var first = items[0];
            var problems = new List<RowError>();

            if (items.Count > BookingRequestValidator.MaxItemsPerAppointment)
                problems.Add(new RowError(first.Row.RowNumber,
                    $"visit '{first.Row.Visit}' has {items.Count} moves; a truck visit can have at most {BookingRequestValidator.MaxItemsPerAppointment}."));

            foreach (var other in items.Skip(1))
            {
                if (other.Date != first.Date || other.Time != first.Time)
                    problems.Add(new RowError(other.Row.RowNumber, $"visit '{other.Row.Visit}' must have one date and time (as on row {first.Row.RowNumber})."));
                if (PlateNumber.Normalize(other.Row.TruckPlate) != PlateNumber.Normalize(first.Row.TruckPlate)
                    || !SameText(other.Row.DriverId, first.Row.DriverId))
                    problems.Add(new RowError(other.Row.RowNumber, $"visit '{other.Row.Visit}' must have one truck and driver (as on row {first.Row.RowNumber})."));
            }

            if (problems.Count > 0)
            {
                errors.AddRange(problems);
                continue;
            }

            visits.Add(new BulkVisit(group.Key, items.Select(i => i.Row).ToList(), first.Date, first.Time, first.Row.TruckPlate, first.Row.DriverId));
        }

        return (visits.OrderBy(v => v.Rows[0].RowNumber).ToList(), errors.OrderBy(e => e.Row).ToList());
    }

    public static bool TryParseDate(string? value, out DateOnly date)
    {
        if (DateOnly.TryParseExact(value?.Trim(), DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
            return true;

        // A raw Excel serial date (days since 1899-12-30), e.g. "46315".
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial) && serial is > 30000 and < 80000)
        {
            date = DateOnly.FromDateTime(DateTime.FromOADate(Math.Floor(serial)));
            return true;
        }
        return false;
    }

    public static bool TryParseTime(string? value, out TimeOnly time)
    {
        if (TimeOnly.TryParseExact(value?.Trim(), TimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out time))
            return true;

        // A raw Excel time (fraction of a day), e.g. "0.416666" = 10:00.
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var fraction) && fraction is >= 0 and < 1)
        {
            time = TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(Math.Min(1439, Math.Round(fraction * 24 * 60))));
            return true;
        }
        return false;
    }

    private static bool SameText(string? a, string? b) =>
        string.Equals(a?.Trim() ?? "", b?.Trim() ?? "", StringComparison.OrdinalIgnoreCase);

    private static bool IsBlank(IReadOnlyList<string> row) => row.All(string.IsNullOrWhiteSpace);

    private static string NormalizeHeader(string header) =>
        new string(header.Trim().TrimStart('﻿').ToLowerInvariant().Select(c => c is ' ' or '-' or '.' ? '_' : c).ToArray()).Trim('_');
}
