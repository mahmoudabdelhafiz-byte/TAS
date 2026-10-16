using Yardsyncs.Application.Booking.Bulk;

namespace Yardsyncs.Application.Tests;

public class CsvTableTests
{
    [Fact]
    public void Reads_quotes_doubled_quotes_and_line_breaks()
    {
        var table = CsvTable.Parse("a,b,c\r\n1,\"x, y\",\"say \"\"hi\"\"\"\n2,\"line\nbreak\",3");

        Assert.Equal(3, table.Count);
        Assert.Equal(new[] { "1", "x, y", "say \"hi\"" }, table[1]);
        Assert.Equal("line\nbreak", table[2][1]);
    }

    [Fact]
    public void Detects_semicolons_and_strips_the_bom()
    {
        var table = CsvTable.Parse("﻿date;time;category\n2026-10-20;10:00;IMP_PICKUP\n");

        Assert.Equal(new[] { "date", "time", "category" }, table[0]);
        Assert.Equal("IMP_PICKUP", table[1][2]);
        Assert.Equal(2, table.Count);
    }
}

public class BulkSheetTests
{
    private static IReadOnlyList<IReadOnlyList<string>> Table(params string[] lines) =>
        lines.Select(l => (IReadOnlyList<string>)l.Split(',')).ToList();

    [Fact]
    public void Maps_columns_by_name_and_alias_in_any_order()
    {
        var (rows, errors) = BulkSheet.Parse(Table(
            "Category,Slot Date,Slot-Time,Container,Plate,Iqama,Tonnage,Group",
            "IMP_PICKUP,2026-10-20,10:00,MSCU1234566,1234 ABJ,1012345678,,V1",
            ",,,,,,,",
            "BB_DELIVERY,20/10/2026,11:00,,,,28.5,"));

        Assert.Empty(errors);
        Assert.Equal(2, rows.Count);
        Assert.Equal(2, rows[0].RowNumber);
        Assert.Equal("MSCU1234566", rows[0].ContainerNo);
        Assert.Equal("1234 ABJ", rows[0].TruckPlate);
        Assert.Equal("1012345678", rows[0].DriverId);
        Assert.Equal("V1", rows[0].Visit);
        Assert.Equal(4, rows[1].RowNumber); // blank row skipped, numbering kept
        Assert.Equal(28.5m, rows[1].PlannedTonnage);
    }

    [Fact]
    public void Unknown_and_missing_columns_are_reported()
    {
        var (rows, errors) = BulkSheet.Parse(Table("date,category,colour", "2026-10-20,IMP_PICKUP,red"));

        Assert.Empty(rows);
        Assert.Contains(errors, e => e.Message.Contains("Unknown column 'colour'"));
        Assert.Contains(errors, e => e.Message.Contains("Missing required column 'time'"));
    }

    [Fact]
    public void Rows_with_the_same_visit_become_one_dual_visit()
    {
        var (rows, _) = BulkSheet.Parse(Table(
            "visit,date,time,category,container_no,truck_plate",
            "v1,2026-10-20,10:00,EXP_DROP,HLXU8700018,1234 ABJ",
            "V1,2026-10-20,10:00,IMP_PICKUP,MSCU1234566,1234abj",
            ",2026-10-20,11:00,MTY_RETURN,MAEU4001110,"));

        var (visits, errors) = BulkSheet.Group(rows);

        Assert.Empty(errors);
        Assert.Equal(2, visits.Count);
        Assert.Equal(2, visits[0].Rows.Count);
        Assert.Equal(new TimeOnly(10, 0), visits[0].Time);
        Assert.Single(visits[1].Rows);
    }

    [Fact]
    public void Visit_rules_and_duplicate_containers_are_checked()
    {
        var (rows, _) = BulkSheet.Parse(Table(
            "visit,date,time,category,container_no,truck_plate",
            "V1,2026-10-20,10:00,EXP_DROP,HLXU8700018,1234 ABJ",
            "V1,2026-10-20,11:00,IMP_PICKUP,MSCU1234566,1234 ABJ",
            "V2,2026-10-20,10:00,EXP_DROP,HLXU8700018,",
            ",tomorrow,10:00,EXP_DROP,MAEU4001110,",
            ",2026-10-20,ten,EXP_DROP,CMAU5100012,"));

        var (visits, errors) = BulkSheet.Group(rows);

        Assert.Contains(errors, e => e.Row == 3 && e.Message.Contains("one date and time"));
        Assert.Contains(errors, e => e.Row == 4 && e.Message.Contains("already on row 2"));
        Assert.Contains(errors, e => e.Row == 5 && e.Message.Contains("not a date"));
        Assert.Contains(errors, e => e.Row == 6 && e.Message.Contains("not a time"));
        Assert.Empty(visits);
    }

    [Fact]
    public void Excel_serial_dates_and_time_fractions_are_understood()
    {
        Assert.True(BulkSheet.TryParseDate("46315", out var date));
        Assert.Equal(new DateOnly(2026, 10, 20), date);
        Assert.True(BulkSheet.TryParseTime("0.4166666667", out var time));
        Assert.Equal(new TimeOnly(10, 0), time);
        Assert.True(BulkSheet.TryParseTime("1000", out var compact));
        Assert.Equal(new TimeOnly(10, 0), compact);
    }

    [Fact]
    public void Too_many_rows_are_refused()
    {
        var lines = new List<string> { "date,time,category" };
        lines.AddRange(Enumerable.Range(0, BulkSheet.MaxRows + 5).Select(_ => "2026-10-20,10:00,IMP_PICKUP"));

        var (rows, errors) = BulkSheet.Parse(Table(lines.ToArray()));

        Assert.Equal(BulkSheet.MaxRows, rows.Count);
        Assert.Contains(errors, e => e.Message.Contains("At most"));
    }
}
