using System.Net;
using System.Net.Http.Json;
using Yardsyncs.Application.Reports;
using Yardsyncs.Domain.Booking;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Yardsyncs.Infrastructure;
using Yardsyncs.Infrastructure.Tos;

namespace Yardsyncs.Api.Tests;

/// <summary>A trucking company's report: the company sees its own, the terminal sees every company.</summary>
public sealed class CompanyReportApiTests(YardsyncsApiFactory factory) : ApiTestBase(factory)
{
    private string Range => $"from={Tomorrow:yyyy-MM-dd}&to={Tomorrow:yyyy-MM-dd}";

    [Fact]
    public async Task A_company_sees_its_trucks_drivers_and_holds_and_only_its_own()
    {
        if (!Enabled) return;

        var alNoor = await As(DemoDataSeeder.DispatcherEmail);
        Assert.Equal(HttpStatusCode.Created, (await alNoor.PostAsJsonAsync(Appointments, new
        {
            slotId = SlotId,
            truckId = Demo.TruckIds[0],
            driverId = Demo.DriverIds[0],
            items = new[] { new { category = "IMP_PICKUP", containerNo = "MSCU1234566" } },
        })).StatusCode);

        var report = await Read<CompanyReport>(await alNoor.GetAsync($"{Terminal}/company-report?{Range}"));
        Assert.Equal((Demo.CompanyId, 1), (report.CompanyId, report.Totals.Appointments));
        Assert.Equal("1234 ABJ", Assert.Single(report.Trucks).Name);
        Assert.Equal("Abdullah Al-Harbi", Assert.Single(report.Drivers).Name);
        Assert.Single(report.Days);

        // Another company's report is not theirs to see.
        Assert.Equal(HttpStatusCode.Forbidden, (await alNoor.GetAsync($"{Terminal}/company-report?companyId={Demo.OtherCompanyId}&{Range}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await alNoor.GetAsync($"{Terminal}/company-report/companies?{Range}")).StatusCode);

        // The terminal sees every company, and any one of them.
        var planner = await As(DemoDataSeeder.PlannerEmail);
        var companies = await Read<List<ReportRow>>(await planner.GetAsync($"{Terminal}/company-report/companies?{Range}"));
        Assert.Equal((Demo.CompanyId.ToString(), 1), (Assert.Single(companies).Key, companies[0].Appointments));
        Assert.Equal(HttpStatusCode.BadRequest, (await planner.GetAsync($"{Terminal}/company-report?{Range}")).StatusCode);
        Assert.Equal(1, (await Read<CompanyReport>(await planner.GetAsync($"{Terminal}/company-report?companyId={Demo.CompanyId}&{Range}"))).Totals.Appointments);
        Assert.Equal(HttpStatusCode.BadRequest, (await planner.GetAsync($"{Terminal}/company-report?companyId={Demo.CompanyId}&from=2026-01-01&to=2026-12-31")).StatusCode);
    }

    [Fact]
    public async Task Holds_are_counted_by_the_check_that_held_the_visit()
    {
        if (!Enabled) return;

        // Booked when clear, then customs holds the container: the visit counts under customs.
        var alNoor = await As(DemoDataSeeder.DispatcherEmail);
        var booked = await alNoor.PostAsJsonAsync(Appointments, ImportBooking(container: "MSCU1234566"));
        Assert.Equal(HttpStatusCode.Created, booked.StatusCode);
        var number = (await Read<Yardsyncs.Application.Booking.AppointmentDto>(booked)).AppointmentNo;
        var tos = Factory.Services.GetRequiredService<IOptionsMonitor<FakeTosOptions>>().CurrentValue;
        tos.CustomsHold.Add("MSCU1234566");
        try
        {
            await alNoor.PostAsync($"{Appointments}/{number}/revalidate", null);
        }
        finally
        {
            tos.CustomsHold.Remove("MSCU1234566");
        }

        var report = await Read<CompanyReport>(await alNoor.GetAsync($"{Terminal}/company-report?{Range}"));
        var hold = Assert.Single(report.Holds);
        Assert.Equal((CheckType.CustomsRelease, 1), (hold.Check, hold.Visits));
        Assert.Contains("MSCU1234566", hold.LastReason);
    }
}
