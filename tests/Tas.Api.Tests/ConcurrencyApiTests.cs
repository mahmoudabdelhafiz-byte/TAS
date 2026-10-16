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
            var container = Container("LDTU", 100 + round);
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

        // 40 import bookings at once: more than the slot takes (on a weekday 15 imports, on Fri / Sat 20 trucks).
        var alNoor = await As(DemoDataSeeder.DispatcherEmail);
        var answers = await Task.WhenAll(Enumerable.Range(0, 40).Select(i =>
            alNoor.PostAsJsonAsync(Appointments, ImportBooking(container: Container("LDTU", i)))));
        Assert.All(answers, a => Assert.Contains(a.StatusCode, new[] { HttpStatusCode.Created, HttpStatusCode.Conflict }));

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TasDbContext>();
        var booked = await db.Appointments.CountAsync(a => a.SlotId == SlotId);
        Assert.Equal(answers.Count(a => a.StatusCode == HttpStatusCode.Created), booked);
        Assert.Contains(answers, a => a.StatusCode == HttpStatusCode.Conflict);
        var slot = await db.Slots.AsNoTracking().SingleAsync(s => s.SlotId == SlotId);
        Assert.Equal(booked, slot.TotalBooked);
        Assert.True(booked <= slot.TotalCapacity - 2, $"{booked} booked, capacity {slot.TotalCapacity} less 2 held for walk-ins");
        var imports = await db.Database.SqlQuery<ImportQuota>($"""
            SELECT booked AS "Booked", quota_capacity AS "Quota" FROM slot_category_capacity c JOIN transaction_category t USING (category_id)
            WHERE c.slot_id = {SlotId} AND t.code = 'IMP_PICKUP'
            """).SingleAsync();
        Assert.Equal(booked, imports.Booked);
        Assert.True(imports.Booked <= imports.Quota);
    }

    private sealed class ImportQuota
    {
        public int Booked { get; set; }
        public int Quota { get; set; }
    }

    /// <summary>A valid ISO 6346 number: owner code + 6-digit serial + check digit.</summary>
    internal static string Container(string owner, int serial)
    {
        var first10 = owner + serial.ToString("D6");
        return first10 + Tas.Application.Booking.ContainerNumber.CheckDigit(first10);
    }
}
