using System.Net;
using System.Net.Http.Json;
using ClosedXML.Excel;
using Yardsyncs.Infrastructure;

namespace Yardsyncs.Api.Tests;

/// <summary>Reports as Excel files: who gets which, and what is in them.</summary>
public sealed class ReportExportApiTests(YardsyncsApiFactory factory) : ApiTestBase(factory)
{
    private string Range => $"from={Tomorrow:yyyy-MM-dd}&to={Tomorrow:yyyy-MM-dd}";

    private static async Task<XLWorkbook> Book(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", response.Content.Headers.ContentType?.MediaType);
        return new XLWorkbook(new MemoryStream(await response.Content.ReadAsByteArrayAsync()));
    }

    [Fact]
    public async Task A_company_downloads_its_own_appointments_and_the_terminal_everything()
    {
        if (!Enabled) return;

        var alNoor = await As(DemoDataSeeder.DispatcherEmail);
        var gulf = await As(DemoDataSeeder.OtherDispatcherEmail);
        Assert.Equal(HttpStatusCode.Created, (await alNoor.PostAsJsonAsync(Appointments, new
        {
            slotId = SlotId,
            truckId = Demo.TruckIds[0],
            driverId = Demo.DriverIds[0],
            items = new[] { new { category = "IMP_PICKUP", containerNo = "MSCU1234566" } },
        })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await gulf.PostAsJsonAsync(Appointments, ImportBooking(container: "MSCU5523417"))).StatusCode);

        using (var mine = await Book(await alNoor.GetAsync($"{Terminal}/exports/appointments?{Range}")))
        {
            var sheet = mine.Worksheet(1);
            Assert.Equal("Appointment", sheet.Cell(1, 1).GetString());
            Assert.Equal(2, sheet.LastRowUsed()!.RowNumber()); // one appointment: their own
            Assert.Equal("Al Noor Transport", sheet.Cell(2, 4).GetString());
            Assert.Equal("1234 ABJ", sheet.Cell(2, 5).GetString());
            Assert.Equal("MSCU1234566", sheet.Cell(2, 8).GetString());
            Assert.Equal(10, sheet.Cell(2, 3).GetDateTime().Hour); // terminal time
        }

        // Another company's appointments and the terminal's KPIs are not theirs.
        Assert.Equal(HttpStatusCode.Forbidden, (await alNoor.GetAsync($"{Terminal}/exports/appointments?companyId={Demo.OtherCompanyId}&{Range}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await alNoor.GetAsync($"{Terminal}/exports/kpis?{Range}")).StatusCode);

        var planner = await As(DemoDataSeeder.PlannerEmail);
        using (var all = await Book(await planner.GetAsync($"{Terminal}/exports/appointments?{Range}")))
            Assert.Equal(3, all.Worksheet(1).LastRowUsed()!.RowNumber());

        // In Arabic: Arabic headings, right to left.
        planner.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ar");
        using (var kpis = await Book(await planner.GetAsync($"{Terminal}/exports/kpis?{Range}")))
        {
            Assert.Equal(new[] { "الملخص", "الأيام", "الساعات", "الشركات" }, kpis.Worksheets.Select(w => w.Name));
            Assert.True(kpis.Worksheet(1).RightToLeft);
            Assert.Equal(3, kpis.Worksheet("الشركات").LastRowUsed()!.RowNumber());
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await planner.GetAsync($"{Terminal}/exports/kpis?from=2026-01-01&to=2026-12-31")).StatusCode);
    }

    [Fact]
    public async Task One_companys_report_as_a_workbook()
    {
        if (!Enabled) return;

        var alNoor = await As(DemoDataSeeder.DispatcherEmail);
        Assert.Equal(HttpStatusCode.Created, (await alNoor.PostAsJsonAsync(Appointments, ImportBooking())).StatusCode);

        using var book = await Book(await alNoor.GetAsync($"{Terminal}/exports/company-report?{Range}"));
        Assert.Equal(new[] { "Summary", "Days", "Trucks", "Drivers", "Holds", "Charges" }, book.Worksheets.Select(w => w.Name));
        Assert.Equal("Al Noor Transport", book.Worksheet("Summary").Cell(2, 2).GetString());

        var planner = await As(DemoDataSeeder.PlannerEmail);
        Assert.Equal(HttpStatusCode.BadRequest, (await planner.GetAsync($"{Terminal}/exports/company-report?{Range}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await planner.GetAsync($"{Terminal}/exports/company-report?companyId={Demo.CompanyId}&{Range}")).StatusCode);
    }
}
