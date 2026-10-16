using System.Net;
using System.Net.Http.Json;
using Yardsyncs.Application.Booking;
using Yardsyncs.Domain.Booking;
using Yardsyncs.Infrastructure;
using Yardsyncs.Infrastructure.Booking;

namespace Yardsyncs.Api.Tests;

/// <summary>Fees: set per terminal, charged against appointments, waived with a reason, reported per company.</summary>
public sealed class ChargeApiTests(YardsyncsApiFactory factory) : ApiTestBase(factory)
{
    private async Task<string> BookAsync(HttpClient dispatcher)
    {
        var booked = await dispatcher.PostAsJsonAsync(Appointments, ImportBooking(Demo.CompanyId));
        Assert.Equal(HttpStatusCode.Created, booked.StatusCode);
        return (await Read<AppointmentDto>(booked)).AppointmentNo;
    }

    [Fact]
    public async Task Fees_are_charged_waived_and_reported_per_company()
    {
        if (!Enabled) return;

        var admin = await As(DemoDataSeeder.TerminalAdminEmail);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"{Terminal}/settings", new
        {
            values = new { fee_booking = 2500, fee_no_show = 10000, fee_late_cancel = 5000, late_cancel_hours = 48 },
        })).StatusCode);

        var dispatcher = await As(DemoDataSeeder.DispatcherEmail);
        Assert.Equal(new FeeSchedule("SAR", 2, 2500, 10000, 5000, 48), await Read<FeeSchedule>(await dispatcher.GetAsync($"{Terminal}/fees")));

        // Booked, then cancelled within 48 hours of the slot: the booking fee and the late-cancellation fee.
        var late = await BookAsync(dispatcher);
        Assert.Equal(HttpStatusCode.OK, (await dispatcher.PostAsJsonAsync($"{Appointments}/{late}/cancel", new { reason = "Changed plans" })).StatusCode);

        // Cancelled in time (no late window now): the booking fee is waived.
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"{Terminal}/settings", new { values = new { late_cancel_hours = 0 } })).StatusCode);
        var inTime = await BookAsync(dispatcher);
        Assert.Equal(HttpStatusCode.OK, (await dispatcher.PostAsJsonAsync($"{Appointments}/{inTime}/cancel", new { reason = "Not needed" })).StatusCode);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var range = $"from={today.AddDays(-1):yyyy-MM-dd}&to={today.AddDays(1):yyyy-MM-dd}";
        var report = await Read<ChargeReport>(await admin.GetAsync($"{Terminal}/charges?{range}"));
        Assert.Equal(3, report.Charges.Count);
        Assert.Equal(new[] { ChargeKind.Booking, ChargeKind.LateCancel }, report.Charges.Where(c => c.AppointmentNo == late).Select(c => c.Kind).Order());
        Assert.Equal("Cancelled in time", Assert.Single(report.Charges, c => c.AppointmentNo == inTime).WaiveReason);
        var alNoor = Assert.Single(report.Companies);
        Assert.Equal((2, 7500L), (alNoor.Charges, alNoor.AmountMinor));
        Assert.Equal(7500, report.TotalMinor);

        // A company sees only its own charges, whatever it asks for.
        var other = await As(DemoDataSeeder.OtherDispatcherEmail);
        Assert.Empty((await Read<ChargeReport>(await other.GetAsync($"{Terminal}/charges?{range}&companyId={Demo.CompanyId}"))).Charges);
        Assert.Equal(3, (await Read<ChargeReport>(await dispatcher.GetAsync($"{Terminal}/charges?{range}"))).Charges.Count);

        // Waiving: the terminal's admins, with a reason, once.
        var lateFee = report.Charges.Single(c => c.Kind == ChargeKind.LateCancel).ChargeId;
        var waive = $"{Terminal}/charges/{lateFee}/waive";
        Assert.Equal(HttpStatusCode.Forbidden, (await (await As(DemoDataSeeder.PlannerEmail)).PostAsJsonAsync(waive, new { reason = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync(waive, new { reason = " " })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync(waive, new { reason = "Crane breakdown, our fault" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync(waive, new { reason = "Again" })).StatusCode);
        Assert.Equal(2500, (await Read<ChargeReport>(await admin.GetAsync($"{Terminal}/charges?{range}"))).TotalMinor);

        // CSV for the terminal's billing system.
        var csv = await admin.GetAsync($"{Terminal}/charges?{range}&format=csv");
        Assert.Equal("text/csv", csv.Content.Headers.ContentType!.MediaType);
        var text = await csv.Content.ReadAsStringAsync();
        Assert.StartsWith("﻿charge_id,appointment_no", text);
        Assert.Contains(",25.00,SAR,", text);
        Assert.Contains("Crane breakdown", text);
    }
}
