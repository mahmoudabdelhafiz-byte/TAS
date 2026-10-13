using System.Net;
using System.Net.Http.Headers;
using System.Text;
using ClosedXML.Excel;
using Tas.Application.Booking;
using Tas.Application.Booking.Bulk;
using Tas.Infrastructure;
using Tas.Infrastructure.Booking;
using Tas.Infrastructure.Capacity;

namespace Tas.Api.Tests;

/// <summary>Bulk booking from CSV and Excel uploads.</summary>
public sealed class BulkBookingApiTests(TasApiFactory factory) : ApiTestBase(factory)
{
    private string D => Tomorrow.ToString("yyyy-MM-dd");

    private async Task<HttpResponseMessage> Upload(HttpClient client, byte[] content, string fileName, bool dryRun, int? companyId = null)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue(fileName.EndsWith(".xlsx") ? "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" : "text/csv");
        form.Add(file, "file", fileName);
        var url = $"{Appointments}/bulk?dryRun={(dryRun ? "true" : "false")}" + (companyId is null ? "" : $"&companyId={companyId}");
        return await client.PostAsync(url, form);
    }

    private static byte[] Csv(params string[] lines) => Encoding.UTF8.GetBytes(string.Join("\r\n", lines));

    /// <summary>A mixed sheet: one dual visit that passes and four visits that fail for different reasons.</summary>
    private byte[] MixedSheet() => Csv(
        "visit,date,time,category,container_no,booking_ref,truck_plate,driver_id",
        $"V1,{D},10:00,EXP_DROP,HLXU8700018,BKG1001,1234 ABJ,1012345678",
        $"V1,{D},10:00,IMP_PICKUP,mscu1234566,,1234abj,1012345678",
        $",{D},10:00,IMP_PICKUP,MSCU7654329,,,",
        $",{D},10:00,IMP_PICKUP,MAEU4001110,,9999 ZZZ,",
        $",{D},10:30,IMP_PICKUP,CMAU5100012,,,",
        $",{D},11:00,IMP_PICKUP,MSCU1234566,,,");

    private static void AssertMixedResults(BulkResult result, BulkVisitStatus okStatus)
    {
        Assert.Equal(5, result.Visits);
        Assert.Equal(1, result.Succeeded);
        Assert.Equal(4, result.Failed);

        var dual = result.Results.Single(r => r.Visit == "V1");
        Assert.Equal(okStatus, dual.Status);
        Assert.Equal(new[] { 2, 3 }, dual.Rows.ToArray());

        Assert.Contains("Customs hold", Assert.Single(result.Results, r => r.Rows.Contains(4)).Errors[0]);
        Assert.Contains("not registered", Assert.Single(result.Results, r => r.Rows.Contains(5)).Errors[0]);
        Assert.Contains("No slot starts", Assert.Single(result.Results, r => r.Rows.Contains(6)).Errors[0]);
        Assert.Contains("already on row 3", Assert.Single(result.Results, r => r.Rows.Contains(7)).Errors[0]);
    }

    [Fact]
    public async Task Preview_checks_every_row_and_books_nothing()
    {
        if (!Enabled) return;

        var dispatcher = await As(DemoDataSeeder.DispatcherEmail);
        var response = await Upload(dispatcher, MixedSheet(), "visits.csv", dryRun: true);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await Read<BulkResult>(response);
        Assert.True(result.DryRun);
        AssertMixedResults(result, BulkVisitStatus.WouldBook);
        Assert.Null(result.Results.Single(r => r.Visit == "V1").AppointmentNo);

        var booked = await Read<List<AppointmentDto>>(await dispatcher.GetAsync($"{Appointments}?date={D}"));
        Assert.Empty(booked);
    }

    [Fact]
    public async Task Real_run_books_the_good_visits_and_reports_the_rest()
    {
        if (!Enabled) return;

        var dispatcher = await As(DemoDataSeeder.DispatcherEmail);
        var result = await Read<BulkResult>(await Upload(dispatcher, MixedSheet(), "visits.csv", dryRun: false));

        Assert.False(result.DryRun);
        AssertMixedResults(result, BulkVisitStatus.Booked);

        var booked = Assert.Single(await Read<List<AppointmentDto>>(await dispatcher.GetAsync($"{Appointments}?date={D}")));
        Assert.Equal(result.Results.Single(r => r.Visit == "V1").AppointmentNo, booked.AppointmentNo);
        Assert.True(booked.IsDual);
        Assert.Equal("1234 ABJ", booked.TruckPlate);
        Assert.Equal("Abdullah Al-Harbi", booked.DriverName);
    }

    [Fact]
    public async Task Preview_counts_capacity_taken_by_earlier_rows()
    {
        if (!Enabled) return;

        var dispatcher = await As(DemoDataSeeder.DispatcherEmail);
        var slots = await Read<List<SlotAvailability>>(await dispatcher.GetAsync($"{Terminal}/availability?date={D}&category=MTY_PICKUP"));
        var free = slots.Single(s => s.StartAt == At(Tomorrow, 10)).Available;
        Assert.True(free > 0);

        // One empty pickup more than fits, booked by release reference only.
        var lines = new List<string> { "date,time,category,booking_ref" };
        lines.AddRange(Enumerable.Range(1, free + 1).Select(i => $"{D},10:00,MTY_PICKUP,REL-{i}"));

        var result = await Read<BulkResult>(await Upload(dispatcher, Csv(lines.ToArray()), "empties.csv", dryRun: true));

        Assert.Equal(free, result.Succeeded);
        var last = Assert.Single(result.Results, r => r.Status == BulkVisitStatus.Failed);
        Assert.Contains("counting earlier rows", last.Errors[0]);
    }

    [Fact]
    public async Task Excel_uploads_with_real_date_cells_work_and_templates_download()
    {
        if (!Enabled) return;

        var dispatcher = await As(DemoDataSeeder.DispatcherEmail);

        // Template downloads.
        var xlsxTemplate = await dispatcher.GetAsync($"{Appointments}/bulk/template");
        Assert.Equal(HttpStatusCode.OK, xlsxTemplate.StatusCode);
        using (var workbook = new XLWorkbook(await xlsxTemplate.Content.ReadAsStreamAsync()))
        {
            var header = workbook.Worksheet("Appointments").Row(1).Cells(1, BulkSheet.Columns.Count).Select(c => c.GetFormattedString()).ToArray();
            Assert.Equal(BulkSheet.Columns.ToArray(), header);
            Assert.NotNull(workbook.Worksheet("Instructions"));
        }
        var csvTemplate = await dispatcher.GetStringAsync($"{Appointments}/bulk/template?format=csv");
        Assert.StartsWith("visit,date,time,category", csvTemplate.TrimStart('﻿'));

        // A sheet where Excel stored the date as a real date and the time as a time.
        byte[] upload;
        using (var workbook = new XLWorkbook())
        {
            var sheet = workbook.Worksheets.Add("My bookings");
            sheet.Cell(1, 1).SetValue("Date");
            sheet.Cell(1, 2).SetValue("Time");
            sheet.Cell(1, 3).SetValue("Category");
            sheet.Cell(1, 4).SetValue("Container No");
            sheet.Cell(2, 1).SetValue(Tomorrow.ToDateTime(TimeOnly.MinValue));
            sheet.Cell(2, 2).SetValue(new TimeSpan(10, 0, 0));
            sheet.Cell(2, 3).SetValue("IMP_PICKUP");
            sheet.Cell(2, 4).SetValue("MAEU4001110");
            using var stream = new MemoryStream();
            workbook.SaveAs(stream);
            upload = stream.ToArray();
        }

        var result = await Read<BulkResult>(await Upload(dispatcher, upload, "bookings.xlsx", dryRun: false));
        var visit = Assert.Single(result.Results);
        Assert.Equal(BulkVisitStatus.Booked, visit.Status);
        Assert.NotNull(visit.AppointmentNo);
    }

    [Fact]
    public async Task Bad_files_and_wrong_companies_are_refused()
    {
        if (!Enabled) return;

        var alNoor = await As(DemoDataSeeder.DispatcherEmail);
        var gulf = await As(DemoDataSeeder.OtherDispatcherEmail);
        var planner = await As(DemoDataSeeder.PlannerEmail);
        var sheet = Csv("date,time,category,container_no", $"{D},10:00,IMP_PICKUP,MAEU4001110");

        Assert.Equal(HttpStatusCode.Forbidden, (await Upload(gulf, sheet, "x.csv", dryRun: true, companyId: Demo.CompanyId)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Upload(planner, sheet, "x.csv", dryRun: true)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Upload(planner, sheet, "x.csv", dryRun: true, companyId: Demo.CompanyId)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Upload(alNoor, sheet, "x.pdf", dryRun: true)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Upload(alNoor, Encoding.UTF8.GetBytes("not really excel"), "x.xlsx", dryRun: true)).StatusCode);

        var badHeader = await Read<BulkResult>(await Upload(alNoor, Csv("when,what", "x,y"), "x.csv", dryRun: true));
        Assert.Equal(0, badHeader.Visits);
        Assert.Contains(badHeader.FileErrors, e => e.Message.Contains("Missing required column 'date'"));

        // Default is a dry run: nothing is booked unless dryRun=false is sent.
        using var form = new MultipartFormDataContent { { new ByteArrayContent(sheet), "file", "x.csv" } };
        var defaulted = await Read<BulkResult>(await alNoor.PostAsync($"{Appointments}/bulk", form));
        Assert.True(defaulted.DryRun);
    }
}
