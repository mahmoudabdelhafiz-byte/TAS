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

        // Editing an area: reshape, rename, block names are unique among the yard blocks.
        var block = $"{areas}/{created.GeofenceId}";
        var bigger = new[] { new[] { 39.18, 21.48 }, new[] { 39.195, 21.48 }, new[] { 39.195, 21.49 }, new[] { 39.18, 21.49 }, new[] { 39.18, 21.48 } };
        Assert.Equal(HttpStatusCode.Forbidden, (await planner.PutAsJsonAsync(block, new { name = "Block C", zoneType = "YARD_ZONE", ring = bigger, yardBlock = "C" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync(block, new { name = "Block C east", zoneType = "YARD_ZONE", ring = bigger, yardBlock = "c" })).StatusCode);
        var edited = (await Read<List<GeofenceView>>(await admin.GetAsync(areas))).Single(a => a.GeofenceId == created.GeofenceId);
        Assert.Equal("Block C east", edited.Name);
        Assert.Equal("C", edited.YardBlock);
        Assert.Contains("39.195", edited.GeoJson);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync(block, new { name = "Block C", zoneType = "YARD_ZONE", ring = bigger, yardBlock = "B2" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync(block, new { name = "Block C", zoneType = "YARD_ZONE", ring = bigger })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync(areas, new { name = "Another A1", zoneType = "YARD_ZONE", ring, yardBlock = "A1" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PutAsJsonAsync($"{areas}/999999", new { name = "X", zoneType = "GATE", ring })).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"{areas}/{created.GeofenceId}/deactivate", null)).StatusCode);
        Assert.False((await Read<List<GeofenceView>>(await admin.GetAsync(areas))).Single(a => a.GeofenceId == created.GeofenceId).IsActive);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PutAsJsonAsync(block, new { name = "Block C", zoneType = "YARD_ZONE", ring = bigger, yardBlock = "C" })).StatusCode);
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

        // The editor reads the roads back as saved, one-way flag included.
        using (var saved = JsonDocument.Parse(await (await planner.GetAsync($"{Terminal}/yard-map/roads")).Content.ReadAsStringAsync()))
        {
            var feature = saved.RootElement.GetProperty("features")[0];
            Assert.True(feature.GetProperty("properties").GetProperty("oneWay").GetBoolean());
            Assert.Equal(2, feature.GetProperty("geometry").GetProperty("coordinates").GetArrayLength());
        }

        var bad = new { type = "FeatureCollection", features = new object[] { new { type = "Feature", geometry = new { type = "Point", coordinates = new[] { 39.17, 21.47 } } } } };
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"{Terminal}/yard-map/roads", new { roads = bad })).StatusCode);
    }

    [Fact]
    public async Task Gate_in_issues_the_ticket_and_pushes_the_block_to_the_app()
    {
        if (!Enabled) return;

        var number = await BookWithAbdullahAsync();
        var phone = await SignInAsync("install-phone-0001");
        Assert.Equal(HttpStatusCode.BadRequest, (await phone.PutAsJsonAsync("/api/driver/push-token", new { token = new string('x', 401) })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await phone.PutAsJsonAsync("/api/driver/push-token", new { token = "fcm-token-abdullah" })).StatusCode);

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TasDbContext>();
            await db.Appointments.Where(a => a.AppointmentNo == number)
                .ExecuteUpdateAsync(x => x.SetProperty(a => a.Status, Tas.Domain.Booking.AppointmentStatus.Arrived));
        }
        var planner = await As(DemoDataSeeder.PlannerEmail);
        Assert.Equal(HttpStatusCode.OK, (await planner.PostAsJsonAsync($"{Terminal}/gate/in", new { appointmentNo = number, laneCode = "IN-1" })).StatusCode);

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TasDbContext>();
            var push = await db.NotificationLogs.AsNoTracking().SingleAsync(n => n.TemplateCode == Tas.Domain.NotificationTemplates.GateIn);
            Assert.Equal(Tas.Domain.NotificationChannel.Push, push.Channel);
            Assert.Equal(Demo.DriverIds[0], push.DriverId);
            Assert.Contains("B2 (14.03.2)", push.Params);
            Assert.Equal(2, await db.YardInstructions.CountAsync(y => db.Appointments.Any(a => a.AppointmentId == y.AppointmentId && a.AppointmentNo == number))); // block B2, then the exit
        }

        // The in-gate ticket in the app.
        var guidance = await Read<YardGuidance>(await phone.GetAsync($"/api/driver/appointments/{number}/yard"));
        Assert.NotNull(guidance.Ticket);
        Assert.Equal("IN-1", guidance.Ticket!.Lane);
        Assert.NotNull(guidance.Ticket.GateInAt);
        Assert.False(string.IsNullOrEmpty(guidance.Ticket.TerminalName));
        Assert.Equal("B2", guidance.Stops[0].Block);

        // Signing out stops pushes to this phone.
        Assert.Equal(HttpStatusCode.NoContent, (await phone.PostAsync("/api/driver/logout", null)).StatusCode);
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TasDbContext>();
            Assert.False(await db.DriverDevices.AnyAsync(d => d.PushToken != null));
        }
    }

    [Fact]
    public async Task Reaching_the_containers_block_tells_the_terminals_systems()
    {
        if (!Enabled) return;

        var number = await BookWithAbdullahAsync(); // import MSCU1234566 in B2, row 14 bay 03 tier 2
        var phone = await SignInAsync("install-phone-0001");
        var position = $"/api/driver/appointments/{number}/position";
        Assert.Equal(HttpStatusCode.Conflict, (await phone.PostAsJsonAsync(position, new { latitude = 21.467, longitude = 39.1735 })).StatusCode);

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TasDbContext>();
            await db.Appointments.Where(a => a.AppointmentNo == number)
                .ExecuteUpdateAsync(x => x.SetProperty(a => a.Status, Tas.Domain.Booking.AppointmentStatus.Arrived));
        }
        var planner = await As(DemoDataSeeder.PlannerEmail);
        Assert.Equal(HttpStatusCode.OK, (await planner.PostAsJsonAsync($"{Terminal}/gate/in", new { appointmentNo = number, laneCode = "IN-1" })).StatusCode);

        // Inside: at the gate nothing yet; driving fast past the block doesn't count; stopping at it does, once.
        Assert.Null((await Read<TripUpdate>(await phone.PostAsJsonAsync(position, new { latitude = 21.4705, longitude = 39.1700 }))).AtStop);
        Assert.Null((await Read<TripUpdate>(await phone.PostAsJsonAsync(position, new { latitude = 21.467, longitude = 39.1735, speed = 9.0 }))).AtStop);
        var at = await Read<TripUpdate>(await phone.PostAsJsonAsync(position, new { latitude = 21.467, longitude = 39.1735, speed = 1.5 }));
        Assert.Equal(1, at.AtStop);
        Assert.Equal(Tas.Domain.Booking.AppointmentStatus.GateIn, at.Status);
        Assert.Null((await Read<TripUpdate>(await phone.PostAsJsonAsync(position, new { latitude = 21.467, longitude = 39.1736 }))).AtStop);

        var events = $"{Terminal}/integration-events";
        Assert.Equal(HttpStatusCode.Forbidden, (await (await As(DemoDataSeeder.DispatcherEmail)).GetAsync(events)).StatusCode);
        var arrived = Assert.Single(await Read<List<Tas.Infrastructure.Integration.IntegrationEventView>>(await planner.GetAsync(events)));
        Assert.Equal("TRUCK_AT_BLOCK", arrived.EventType);
        Assert.Equal(number, arrived.AppointmentNo);
        Assert.Equal("QUEUED", arrived.Status);
        var stop = arrived.Payload.GetProperty("stop");
        Assert.Equal("B2", stop.GetProperty("block").GetString());
        Assert.Equal("14.03.2", stop.GetProperty("slot").GetString());
        Assert.Equal("MSCU1234566", stop.GetProperty("container").GetString());
        Assert.Equal("PICKUP", stop.GetProperty("kind").GetString());
        Assert.Equal("DEMO", arrived.Payload.GetProperty("terminal").GetString());
        Assert.Equal(21.467, arrived.Payload.GetProperty("position").GetProperty("latitude").GetDouble());

        var yard = $"/api/driver/appointments/{number}/yard";
        var guidance = await Read<YardGuidance>(await phone.GetAsync(yard));
        Assert.True(guidance.Stops[0].Arrived);
        Assert.False(guidance.Stops[0].Done);

        // "Done here": confirmed once, the exit isn't a stop to confirm.
        Assert.Equal(HttpStatusCode.NoContent, (await phone.PostAsync($"/api/driver/appointments/{number}/stops/1/done", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await phone.PostAsync($"/api/driver/appointments/{number}/stops/1/done", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await phone.PostAsync($"/api/driver/appointments/{number}/stops/2/done", null)).StatusCode);
        Assert.True((await Read<YardGuidance>(await phone.GetAsync(yard))).Stops[0].Done);
        var all = await Read<List<Tas.Infrastructure.Integration.IntegrationEventView>>(await planner.GetAsync(events));
        Assert.Equal(new[] { "STOP_COMPLETED", "TRUCK_AT_BLOCK" }, all.Select(e => e.EventType).ToArray());
    }

    [Fact]
    public async Task The_control_room_follows_the_truck_from_the_gate_to_the_block()
    {
        if (!Enabled) return;

        var number = await BookWithAbdullahAsync();
        var phone = await SignInAsync("install-phone-0001");
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TasDbContext>();
            await db.Appointments.Where(a => a.AppointmentNo == number)
                .ExecuteUpdateAsync(x => x.SetProperty(a => a.Status, Tas.Domain.Booking.AppointmentStatus.Arrived));
        }
        var planner = await As(DemoDataSeeder.PlannerEmail);
        var room = $"{Terminal}/control-room";
        Assert.Equal(HttpStatusCode.Forbidden, (await (await As(DemoDataSeeder.DispatcherEmail)).GetAsync(room)).StatusCode);

        var atGate = await Read<Tas.Infrastructure.ControlRoom.ControlRoomBoard>(await planner.GetAsync(room));
        Assert.Equal(1, atGate.Stats.AtGate);
        Assert.Equal("AT_GATE", Assert.Single(atGate.Trucks).Stage);
        Assert.Equal(60, atGate.TurnTargetMinutes);
        Assert.Equal(4, atGate.Map.Areas.Count(a => a.Kind == "YARD_ZONE"));

        Assert.Equal(HttpStatusCode.OK, (await planner.PostAsJsonAsync($"{Terminal}/gate/in", new { appointmentNo = number, laneCode = "IN-1" })).StatusCode);
        var inside = Assert.Single((await Read<Tas.Infrastructure.ControlRoom.ControlRoomBoard>(await planner.GetAsync(room))).Trucks);
        Assert.Equal("TO_BLOCK", inside.Stage);
        Assert.Equal(1, inside.CurrentStop);
        Assert.Equal("B2", inside.Stops[0].Block);
        Assert.InRange(inside.MinutesInside!.Value, 0, 1);
        Assert.False(inside.Overdue);

        var position = $"/api/driver/appointments/{number}/position";
        Assert.Equal(1, (await Read<TripUpdate>(await phone.PostAsJsonAsync(position, new { latitude = 21.467, longitude = 39.1735, speed = 1.0 }))).AtStop);
        var board = await Read<Tas.Infrastructure.ControlRoom.ControlRoomBoard>(await planner.GetAsync(room));
        var atBlock = Assert.Single(board.Trucks);
        Assert.Equal("AT_BLOCK", atBlock.Stage);
        Assert.Equal(new[] { 39.1735, 21.467 }, atBlock.Position);
        Assert.Equal(1, board.Stats.Inside);
        Assert.InRange(board.Stats.AvgMinutesToBlock!.Value, 0, 2);

        Assert.Equal(HttpStatusCode.NoContent, (await phone.PostAsync($"/api/driver/appointments/{number}/stops/1/done", null)).StatusCode);
        var leaving = Assert.Single((await Read<Tas.Infrastructure.ControlRoom.ControlRoomBoard>(await planner.GetAsync(room))).Trucks);
        Assert.Equal("TO_EXIT", leaving.Stage);
        Assert.Null(leaving.CurrentStop);
        Assert.Equal("DONE", leaving.Stops[0].Status);

        // The KPI report has the times inside the terminal (the app saw the arrival; confirmed straight after).
        var day = Tomorrow.ToString("yyyy-MM-dd");
        var kpi = await Read<Tas.Application.Reports.KpiReport>(await planner.GetAsync($"{Terminal}/kpis?from={day}&to={day}"));
        Assert.NotNull(kpi.Yard);
        Assert.Equal(1, kpi.Yard!.Visits);
        Assert.Equal(1.0, kpi.Yard.AppSeenShare);
        Assert.Equal("B2", Assert.Single(kpi.Yard.Blocks).Block);

        // A turn-time target from the terminal's settings.
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TasDbContext>();
            db.TerminalSettings.Add(new Tas.Domain.TerminalSetting { TerminalId = Demo.TerminalId, SettingKey = "turn_target_min", SettingValue = "45" });
            await db.SaveChangesAsync();
        }
        Assert.Equal(45, (await Read<Tas.Infrastructure.ControlRoom.ControlRoomBoard>(await planner.GetAsync(room))).TurnTargetMinutes);
    }
}

