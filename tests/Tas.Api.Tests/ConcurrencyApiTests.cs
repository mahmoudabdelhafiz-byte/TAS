using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tas.Infrastructure;
using Tas.Infrastructure.Persistence;

namespace Tas.Api.Tests;

/// <summary>What the load test showed: bookings sent at the same moment must not break the rules.</summary>
public sealed class ConcurrencyApiTests(TasApiFactory factory) : ApiTestBase(factory)
{
    [Fact]
    public async Task The_same_container_booked_twice_at_once_gets_one_appointment()
    {
        if (!Enabled) return;

        var alNoor = await As(DemoDataSeeder.DispatcherEmail);
        var gulf = await As(DemoDataSeeder.OtherDispatcherEmail);
        for (var round = 0; round < 5; round++)
        {
            var container = round switch { 0 => "MSCU1234566", 1 => "MSCU5523417", 2 => "TGHU8812340", 3 => "MEDU2601003", _ => "CMAU5100028" };
            var answers = await Task.WhenAll(
                alNoor.PostAsJsonAsync(Appointments, ImportBooking(container: container)),
                gulf.PostAsJsonAsync(Appointments, ImportBooking(container: container)),
                alNoor.PostAsJsonAsync(Appointments, ImportBooking(container: container)));
            Assert.Single(answers, a => a.StatusCode == HttpStatusCode.Created);
            Assert.All(answers.Where(a => a.StatusCode != HttpStatusCode.Created), a => Assert.Equal(HttpStatusCode.Conflict, a.StatusCode));
        }

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TasDbContext>();
        Assert.Equal(5, await db.Appointments.CountAsync(a => a.SlotId == SlotId));
        Assert.Equal(5, await db.Slots.Where(s => s.SlotId == SlotId).Select(s => s.TotalBooked).SingleAsync());
    }

    [Fact]
    public async Task A_slot_rush_never_books_past_capacity()
    {
        if (!Enabled) return;

        // 40 import bookings at once for a slot that takes 15 imports (and 31 trucks).
        var alNoor = await As(DemoDataSeeder.DispatcherEmail);
        var answers = await Task.WhenAll(Enumerable.Range(0, 40).Select(i =>
            alNoor.PostAsJsonAsync(Appointments, ImportBooking(container: Container("LDTU", i)))));
        Assert.All(answers, a => Assert.Contains(a.StatusCode, new[] { HttpStatusCode.Created, HttpStatusCode.Conflict }));

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TasDbContext>();
        var booked = await db.Appointments.CountAsync(a => a.SlotId == SlotId);
        Assert.Equal(answers.Count(a => a.StatusCode == HttpStatusCode.Created), booked);
        Assert.InRange(booked, 1, 15);
        Assert.Equal(booked, await db.Slots.Where(s => s.SlotId == SlotId).Select(s => s.TotalBooked).SingleAsync());
        Assert.Equal(booked, await db.Database.SqlQuery<int>($"""
            SELECT booked AS "Value" FROM slot_category_capacity c JOIN transaction_category t USING (category_id)
            WHERE c.slot_id = {SlotId} AND t.code = 'IMP_PICKUP'
            """).SingleAsync());
    }

    /// <summary>A valid ISO 6346 number: owner code + 6-digit serial + check digit.</summary>
    internal static string Container(string owner, int serial)
    {
        var first10 = owner + serial.ToString("D6");
        return first10 + Tas.Application.Booking.ContainerNumber.CheckDigit(first10);
    }
}
