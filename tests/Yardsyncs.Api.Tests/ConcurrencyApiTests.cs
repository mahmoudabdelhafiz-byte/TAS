using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yardsyncs.Infrastructure;
using Yardsyncs.Infrastructure.Persistence;

namespace Yardsyncs.Api.Tests;

/// <summary>What the load test showed: bookings sent at the same moment must not break the rules.</summary>
public sealed class ConcurrencyApiTests(YardsyncsApiFactory factory) : ApiTestBase(factory)
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
        var db = scope.ServiceProvider.GetRequiredService<YardsyncsDbContext>();
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
        var db = scope.ServiceProvider.GetRequiredService<YardsyncsDbContext>();
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

    [Fact]
    public async Task Moves_in_opposite_directions_at_once_do_not_deadlock()
    {
        if (!Enabled) return;

        long nextSlot;
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<YardsyncsDbContext>();
            var start = At(Tomorrow, 11).ToUniversalTime();
            nextSlot = await db.Slots.Where(s => s.TerminalId == Demo.TerminalId && s.StartAt == start).Select(s => s.SlotId).SingleAsync();
        }

        var alNoor = await As(DemoDataSeeder.DispatcherEmail);
        async Task<string> BookAsync(long slot, int serial)
        {
            var r = await alNoor.PostAsJsonAsync(Appointments, ImportBooking(container: Container("LDTU", serial), slotId: slot));
            Assert.Equal(HttpStatusCode.Created, r.StatusCode);
            return (await Read<Yardsyncs.Application.Booking.AppointmentDto>(r)).AppointmentNo;
        }
        var at10 = new List<string>();
        var at11 = new List<string>();
        for (var i = 0; i < 6; i++)
        {
            at10.Add(await BookAsync(SlotId, 200 + i));
            at11.Add(await BookAsync(nextSlot, 300 + i));
        }

        // Six trucks 10→11 and six 11→10, all at the same moment.
        var moves = at10.Select(no => alNoor.PostAsJsonAsync($"{Appointments}/{no}/reschedule", new { slotId = nextSlot }))
            .Concat(at11.Select(no => alNoor.PostAsJsonAsync($"{Appointments}/{no}/reschedule", new { slotId = SlotId })));
        var answers = await Task.WhenAll(moves);
        Assert.All(answers, a => Assert.Contains(a.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }));

        using var check = Factory.Services.CreateScope();
        var counts = check.ServiceProvider.GetRequiredService<YardsyncsDbContext>();
        foreach (var slot in new[] { SlotId, nextSlot })
            Assert.Equal(await counts.Appointments.CountAsync(a => a.SlotId == slot),
                await counts.Slots.Where(s => s.SlotId == slot).Select(s => s.TotalBooked).SingleAsync());
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
        return first10 + Yardsyncs.Application.Booking.ContainerNumber.CheckDigit(first10);
    }
}
