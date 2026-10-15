using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tas.Infrastructure;
using Tas.Application.Gate;
using Tas.Infrastructure.Persistence;
using Tas.Infrastructure.DriverApp;

namespace Tas.Api.Tests;

/// <summary>Driver app: sign-in with a code, my appointments, start trip, one phone at a time.</summary>
public sealed class DriverAppApiTests(TasApiFactory factory) : ApiTestBase(factory)
{
    private const string Abdullah = "0500000001"; // demo driver, +966500000001

    private async Task<string?> StartAsync(string mobile, string installId)
    {
        var response = await Factory.CreateClient().PostAsJsonAsync("/api/driver/login/start", new { mobile, installId });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("testCode").GetString();
    }

    private async Task<HttpClient> SignInAsync(string installId)
    {
        var code = await StartAsync(Abdullah, installId);
        var response = await Factory.CreateClient().PostAsJsonAsync("/api/driver/login/verify",
            new { mobile = Abdullah, installId, code, platform = "android", appVersion = "0.1.0" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var session = await Read<DriverSession>(response);
        Assert.Equal("Abdullah Al-Harbi", session.Driver.FullName);
        Assert.Equal("+966500000001", session.Driver.Mobile);

        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
        return client;
    }

    [Fact]
    public async Task Sign_in_sees_own_appointments_and_starts_the_trip()
    {
        if (!Enabled) return;

        // Al Noor books a truck with Abdullah as the driver, two days ahead (too early to start the trip).
        long slotId;
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TasDbContext>();
            var start = At(Tomorrow.AddDays(1), 10).ToUniversalTime();
            slotId = await db.Slots.Where(x => x.TerminalId == Demo.TerminalId && x.StartAt == start).Select(x => x.SlotId).SingleAsync();
        }
        var alNoor = await As(DemoDataSeeder.DispatcherEmail);
        var booked = await alNoor.PostAsJsonAsync(Appointments, new
        {
            slotId,
            driverId = Demo.DriverIds[0],
            items = new[] { new { category = "IMP_PICKUP", containerNo = "MSCU1234566" } },
        });
        Assert.Equal(HttpStatusCode.Created, booked.StatusCode);
        var number = (await Read<Tas.Application.Booking.AppointmentDto>(booked)).AppointmentNo;

        var driver = await SignInAsync("install-phone-0001");

        var list = await Read<List<DriverAppointment>>(await driver.GetAsync("/api/driver/appointments"));
        var mine = Assert.Single(list);
        Assert.Equal(number, mine.AppointmentNo);
        Assert.Equal("استلام حاوية وارد", Assert.Single(mine.Moves).CategoryAr);
        Assert.NotNull(mine.GateCode);
        Assert.Equal(180, mine.UtcOffsetMinutes);

        Assert.Equal(HttpStatusCode.OK, (await driver.GetAsync($"/api/driver/appointments/{number}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await driver.GetAsync("/api/driver/appointments/DEMO-000000-NOPE1")).StatusCode);

        // Too early to start the trip (allowed from 12 hours before the slot).
        var early = await driver.PostAsync($"/api/driver/appointments/{number}/start-trip", null);
        Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
        Assert.Contains("Too early", await early.Content.ReadAsStringAsync());

        // Driver tokens don't open staff endpoints, and staff tokens don't open driver endpoints.
        Assert.Equal(HttpStatusCode.Unauthorized, (await driver.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await alNoor.GetAsync("/api/driver/appointments")).StatusCode);
    }

    [Fact]
    public async Task Codes_are_single_use_limited_and_one_phone_at_a_time()
    {
        if (!Enabled) return;

        // Unknown numbers get the same answer (nothing is sent).
        Assert.Null(await StartAsync("0599999999", "install-unknown-01"));

        // Wrong code, then the right one; the code can't be used twice.
        var code = await StartAsync(Abdullah, "install-phone-0001");
        var anon = Factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/api/driver/login/verify",
            new { mobile = Abdullah, installId = "install-phone-0001", code = code == "000000" ? "111111" : "000000" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/api/driver/login/verify",
            new { mobile = Abdullah, installId = "install-other-0001", code })).StatusCode); // another phone can't use it
        Assert.Equal(HttpStatusCode.OK, (await anon.PostAsJsonAsync("/api/driver/login/verify",
            new { mobile = Abdullah, installId = "install-phone-0001", code })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/api/driver/login/verify",
            new { mobile = Abdullah, installId = "install-phone-0001", code })).StatusCode);

        // Signing in on a second phone signs the first one out (2 more codes: 3 in 15 minutes is the limit).
        var first = await SignInAsync("install-phone-0002");
        Assert.Equal(HttpStatusCode.OK, (await first.GetAsync("/api/driver/me")).StatusCode);
        var second = await SignInAsync("install-phone-0003");
        Assert.Equal(HttpStatusCode.OK, (await second.GetAsync("/api/driver/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await first.GetAsync("/api/driver/me")).StatusCode);

        // Fourth code within 15 minutes is refused.
        Assert.Equal(HttpStatusCode.TooManyRequests, (await anon.PostAsJsonAsync("/api/driver/login/start",
            new { mobile = Abdullah, installId = "install-phone-0004" })).StatusCode);

        // Logging out ends the session.
        Assert.Equal(HttpStatusCode.NoContent, (await second.PostAsync("/api/driver/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await second.GetAsync("/api/driver/me")).StatusCode);
    }

    [Fact]
    public async Task Blocked_drivers_cannot_sign_in()
    {
        if (!Enabled) return;

        var admin = await As(DemoDataSeeder.TerminalAdminEmail);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync(
            $"/api/companies/{Demo.CompanyId}/drivers/{Demo.DriverIds[0]}/block", new { blocked = true, reason = "Unsafe driving" })).StatusCode);

        var code = await StartAsync(Abdullah, "install-phone-0001");
        Assert.Null(code); // treated like an unknown number
        Assert.Equal(HttpStatusCode.Unauthorized, (await Factory.CreateClient().PostAsJsonAsync("/api/driver/login/verify",
            new { mobile = Abdullah, installId = "install-phone-0001", code = "123456" })).StatusCode);
    }

    private async Task<string> BookWithAbdullahAsync()
    {
        var alNoor = await As(DemoDataSeeder.DispatcherEmail);
        var booked = await alNoor.PostAsJsonAsync(Appointments, new
        {
            slotId = SlotId,
            driverId = Demo.DriverIds[0],
            items = new[] { new { category = "IMP_PICKUP", containerNo = "MSCU1234566" } },
        });
        Assert.Equal(HttpStatusCode.Created, booked.StatusCode);
        return (await Read<Tas.Application.Booking.AppointmentDto>(booked)).AppointmentNo;
    }

    private static async Task<(long PassId, byte[] Secret)> PassAsync(HttpClient driver)
    {
        var pass = await Read<GatePassView>(await driver.GetAsync("/api/driver/gate-pass"));
        Assert.Equal(GatePassCode.StepSeconds, pass.StepSeconds);
        return (pass.PassId, Convert.FromBase64String(pass.Secret));
    }

    private async Task<Tas.Application.Gate.ArrivalResult> ArriveAsync(string qrToken)
    {
        var planner = await As(DemoDataSeeder.PlannerEmail);
        var response = await planner.PostAsJsonAsync($"{Terminal}/gate/arrivals", new { laneCode = "IN-1", qrToken });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await Read<Tas.Api.AdminResponse<Tas.Application.Gate.ArrivalResult>>(response)).Data;
    }

    [Fact]
    public async Task The_rotating_gate_pass_identifies_the_appointment_at_the_lane()
    {
        if (!Enabled) return;

        var number = await BookWithAbdullahAsync();
        var phone = await SignInAsync("install-phone-0001");
        var (passId, secret) = await PassAsync(phone);
        Assert.Equal(passId, (await PassAsync(phone)).PassId); // same pass until the next sign-in

        var live = GatePassCode.Payload(passId, number, GatePassCode.Compute(secret, GatePassCode.Step(DateTimeOffset.UtcNow)));
        var matched = await ArriveAsync(live);
        Assert.Equal(number, matched.AppointmentNo);
        Assert.Equal(MatchMethod.AppointmentNo, matched.MatchedBy);

        var stale = GatePassCode.Payload(passId, number, GatePassCode.Compute(secret, GatePassCode.Step(DateTimeOffset.UtcNow) - 10));
        var refused = await ArriveAsync(stale);
        Assert.Null(refused.AppointmentNo);
        Assert.Contains(refused.Warnings, w => w.Contains("expired"));

        // Signing in on another phone revokes the old pass.
        await SignInAsync("install-phone-0002");
        var revoked = await ArriveAsync(GatePassCode.Payload(passId, number, GatePassCode.Compute(secret, GatePassCode.Step(DateTimeOffset.UtcNow))));
        Assert.Null(revoked.AppointmentNo);
        Assert.Contains(revoked.Warnings, w => w.Contains("no longer valid"));
    }

    [Fact]
    public async Task Positions_on_the_way_give_an_arrival_estimate_and_check_in_at_the_pre_gate()
    {
        if (!Enabled) return;

        var number = await BookWithAbdullahAsync();
        var phone = await SignInAsync("install-phone-0001");
        var position = $"/api/driver/appointments/{number}/position";

        // Not on the way yet: no position accepted.
        Assert.Equal(HttpStatusCode.Conflict, (await phone.PostAsJsonAsync(position, new { latitude = 21.30, longitude = 39.10 })).StatusCode);

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TasDbContext>();
            await db.Appointments.Where(a => a.AppointmentNo == number)
                .ExecuteUpdateAsync(x => x.SetProperty(a => a.Status, Tas.Domain.Booking.AppointmentStatus.EnRoute));
        }

        // About 20 km away: an estimate, still on the way.
        var far = await Read<TripUpdate>(await phone.PostAsJsonAsync(position, new { latitude = 21.30, longitude = 39.10, speed = 15.0 }));
        Assert.Equal(Tas.Domain.Booking.AppointmentStatus.EnRoute, far.Status);
        Assert.NotNull(far.Eta);
        Assert.InRange(far.DistanceKm!.Value, 15, 25);
        Assert.False(far.CheckedIn);

        var staff = await As(DemoDataSeeder.PlannerEmail);
        var seen = await Read<Tas.Application.Booking.AppointmentDto>(await staff.GetAsync($"{Appointments}/{number}"));
        Assert.Equal(far.Eta!.Value.ToUnixTimeSeconds(), seen.Eta!.Value.ToUnixTimeSeconds());

        // In the pre-gate parking: checked in.
        var there = await Read<TripUpdate>(await phone.PostAsJsonAsync(position, new { latitude = 21.4717, longitude = 39.1700 }));
        Assert.True(there.CheckedIn);
        Assert.Equal(Tas.Domain.Booking.AppointmentStatus.Arrived, there.Status);
        Assert.Contains("PRE_GATE", there.Areas);
        Assert.Contains("APPROACH", there.Areas);

        // Arrived: positions are no longer taken.
        Assert.Equal(HttpStatusCode.Conflict, (await phone.PostAsJsonAsync(position, new { latitude = 21.4717, longitude = 39.1700 })).StatusCode);
    }

    [Fact]
    public async Task Terminal_admins_draw_areas()
    {
        if (!Enabled) return;

        var admin = await As(DemoDataSeeder.TerminalAdminEmail);
        var planner = await As(DemoDataSeeder.PlannerEmail);
        var areas = $"{Terminal}/geofences";

        var list = await Read<List<GeofenceView>>(await planner.GetAsync(areas));
        Assert.Equal(8, list.Count); // approach, pre-gate, gate, exit and four yard blocks
        Assert.Contains("Polygon", list[0].GeoJson);
        Assert.True(list.Single(a => a.ZoneType == "PRE_GATE").TriggersCheckIn);

        var ring = new[] { new[] { 39.18, 21.48 }, new[] { 39.19, 21.48 }, new[] { 39.19, 21.49 }, new[] { 39.18, 21.49 } };
        Assert.Equal(HttpStatusCode.Forbidden, (await planner.PostAsJsonAsync(areas, new { name = "Block C", zoneType = "YARD_ZONE", ring })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync(areas, new { name = "Block C", zoneType = "YARD_ZONE", ring, yardBlock = "C" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync(areas, new { name = "Bad", zoneType = "YARD_ZONE", ring = ring.Take(2) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync(areas, new { name = "Bad", zoneType = "MOON", ring })).StatusCode);

        var created = (await Read<List<GeofenceView>>(await admin.GetAsync(areas))).Single(a => a.Name == "Block C");
        Assert.Equal("C", created.YardBlock);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"{areas}/{created.GeofenceId}/deactivate", null)).StatusCode);
        Assert.False((await Read<List<GeofenceView>>(await admin.GetAsync(areas))).Single(a => a.Name == "Block C").IsActive);
    }

    [Fact]
    public async Task After_gate_in_the_driver_gets_stops_and_routes_through_the_yard()
    {
        if (!Enabled) return;

        // Dual visit: drop an export, then pick up an import.
        var alNoor = await As(DemoDataSeeder.DispatcherEmail);
        var booked = await alNoor.PostAsJsonAsync(Appointments, new
        {
            slotId = SlotId,
            driverId = Demo.DriverIds[0],
            items = new object[]
            {
                new { category = "IMP_PICKUP", containerNo = "MSCU1234566" },
                new { category = "EXP_DROP", containerNo = "MSCU5523417" },
            },
        });
        Assert.Equal(HttpStatusCode.Created, booked.StatusCode);
        var number = (await Read<Tas.Application.Booking.AppointmentDto>(booked)).AppointmentNo;
        var phone = await SignInAsync("install-phone-0001");
        var yard = $"/api/driver/appointments/{number}/yard";

        // Not inside yet: no positions.
        var early = await phone.GetAsync(yard);
        Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
        Assert.Contains("after you pass the gate", await early.Content.ReadAsStringAsync());

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TasDbContext>();
            await db.Appointments.Where(a => a.AppointmentNo == number)
                .ExecuteUpdateAsync(x => x.SetProperty(a => a.Status, Tas.Domain.Booking.AppointmentStatus.GateIn));
        }

        var guidance = await Read<YardGuidance>(await phone.GetAsync(yard));
        Assert.True(guidance.Live);
        Assert.Equal(new[] { "DROP", "PICKUP", "EXIT" }, guidance.Stops.Select(x => x.Kind).ToArray());
        Assert.Equal("E1", guidance.Stops[0].Block);
        Assert.Equal("B2", guidance.Stops[1].Block);
        Assert.Equal("14.03.2", guidance.Stops[1].Slot);
        Assert.Equal("MSCU1234566", Assert.Single(guidance.Stops[1].Containers));
        Assert.All(guidance.Stops, x => Assert.False(x.Moved));

        // A route for each stop and the exit, starting at the gate, along the roads.
        Assert.Equal(3, guidance.Legs.Count);
        Assert.Equal(new[] { 39.170, 21.4700 }, guidance.Legs[0].Path[0]);
        Assert.All(guidance.Legs, leg => Assert.True(leg.Path.Count >= 2 && leg.Meters > 0));
        Assert.Equal(new[] { 39.176, 21.4625 }, guidance.Legs[2].Path[^1]);
        Assert.Equal(4, guidance.Map.Areas.Count(a => a.Kind == "YARD_ZONE"));
        Assert.Equal(6, guidance.Map.Roads.Count);

        // If the container is moved in the yard, the driver is told.
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TasDbContext>();
            await db.YardInstructions.Where(y => y.Sequence == 2).ExecuteUpdateAsync(x => x.SetProperty(y => y.Block, "A1"));
        }
        var again = await Read<YardGuidance>(await phone.GetAsync(yard));
        Assert.True(again.Stops[1].Moved);
        Assert.False(again.Stops[0].Moved);
    }

    [Fact]
    public async Task Terminal_admins_draw_the_roads()
    {
        if (!Enabled) return;

        var admin = await As(DemoDataSeeder.TerminalAdminEmail);
        var planner = await As(DemoDataSeeder.PlannerEmail);
        var map = await Read<YardMapView>(await planner.GetAsync($"{Terminal}/yard-map"));
        Assert.Equal(6, map.Roads.Count);
        Assert.True(map.Bounds[2] > map.Bounds[0]);

        var roads = new
        {
            type = "FeatureCollection",
            features = new object[]
            {
                new { type = "Feature", properties = new { oneWay = true }, geometry = new { type = "LineString", coordinates = new[] { new[] { 39.170, 21.470 }, new[] { 39.170, 21.465 } } } },
            },
        };
        Assert.Equal(HttpStatusCode.Forbidden, (await planner.PutAsJsonAsync($"{Terminal}/yard-map/roads", new { roads })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync($"{Terminal}/yard-map/roads", new { roads })).StatusCode);
        Assert.Single((await Read<YardMapView>(await planner.GetAsync($"{Terminal}/yard-map"))).Roads);

        var bad = new { type = "FeatureCollection", features = new object[] { new { type = "Feature", geometry = new { type = "Point", coordinates = new[] { 39.17, 21.47 } } } } };
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"{Terminal}/yard-map/roads", new { roads = bad })).StatusCode);
    }
}
