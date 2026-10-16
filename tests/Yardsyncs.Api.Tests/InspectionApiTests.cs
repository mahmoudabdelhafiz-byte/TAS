using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Yardsyncs.Api;
using Yardsyncs.Application.Booking;
using Yardsyncs.Application.Gate;
using Yardsyncs.Domain.Gate;
using Yardsyncs.Infrastructure;
using Yardsyncs.Infrastructure.Gate;

namespace Yardsyncs.Api.Tests;

/// <summary>Seals, damage and photos at the gate; companies' disputes.</summary>
public sealed class InspectionApiTests(YardsyncsApiFactory factory) : ApiTestBase(factory)
{
    private string Gate => $"{Terminal}/gate";

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52, 1, 2, 3, 4];

    private static MultipartFormDataContent Form(byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        return new MultipartFormDataContent { { content, "file", "door.png" } };
    }

    [Fact]
    public async Task Seal_and_damage_are_checked_at_the_gate_and_the_company_can_dispute()
    {
        if (!Enabled) return;
        var dispatcher = await As(DemoDataSeeder.DispatcherEmail);
        var other = await As(DemoDataSeeder.OtherDispatcherEmail);
        var planner = await As(DemoDataSeeder.PlannerEmail);
        var booked = await Read<AppointmentDto>(await dispatcher.PostAsJsonAsync(Appointments, new
        {
            slotId = SlotId, companyId = Demo.CompanyId, truckId = Demo.TruckIds[0],
            items = new[] { new { category = "IMP_PICKUP", containerNo = "MSCU1234566" } },
        }));
        var path = $"{Appointments}/{booked.AppointmentNo}";

        // The company declares the seal; another company can't even see the visit.
        Assert.Equal(HttpStatusCode.OK, (await dispatcher.PutAsJsonAsync($"{path}/items/1/seal", new { sealNo = "ml-449 120" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PutAsJsonAsync($"{path}/items/1/seal", new { sealNo = "X1" })).StatusCode);
        Assert.Equal("ML449120", (await Read<AppointmentInspections>(await dispatcher.GetAsync($"{path}/inspections"))).Items.Single().SealNo);

        // Not at the gate yet: nothing to inspect.
        Assert.Equal(HttpStatusCode.Conflict, (await planner.PostAsJsonAsync($"{path}/inspections", new { stage = "In", containerNo = "MSCU1234566" })).StatusCode);

        // Let in from the trouble desk, then inspected: another seal and a dent.
        var arrival = (await Read<AdminResponse<ArrivalResult>>(await planner.PostAsJsonAsync($"{Gate}/arrivals", new { laneCode = "IN-1", appointmentNo = booked.AppointmentNo }))).Data;
        await planner.PostAsJsonAsync($"{Gate}/troubles/{arrival.TroubleId}/resolve", new { action = "Admit", note = "Planner agreed", appointmentNo = booked.AppointmentNo });
        Assert.Equal(HttpStatusCode.Conflict, (await dispatcher.PutAsJsonAsync($"{path}/items/1/seal", new { sealNo = "ML1" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await dispatcher.PostAsJsonAsync($"{path}/inspections", new { stage = "In" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await planner.PostAsJsonAsync($"{path}/inspections", new { stage = "In", containerNo = "CSQU3054383" })).StatusCode);

        var saved = await planner.PostAsJsonAsync($"{path}/inspections", new
        {
            stage = "In", containerNo = "mscu 1234566", sealSeen = "ML449121",
            damage = new[] { new { side = "Door", kind = "Dent", severity = "Minor", note = "Small dent, right door" } },
        });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var gateIn = (await Read<AdminResponse<InspectionView>>(saved)).Data;
        Assert.Equal((SealStatus.Mismatch, true, InspectionSource.Clerk), (gateIn.SealStatus, gateIn.DamageFound, gateIn.Source));

        // A photo, seen by the visit's company only; not an image is refused.
        var upload = await planner.PostAsync($"{Terminal}/inspections/{gateIn.InspectionId}/photos", Form(Png));
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        var photoId = (await Read<AdminResponse<long>>(upload)).Data;
        Assert.Equal(HttpStatusCode.BadRequest, (await planner.PostAsync($"{Terminal}/inspections/{gateIn.InspectionId}/photos", Form("not a picture"u8.ToArray()))).StatusCode);
        var photo = await dispatcher.GetAsync($"{Terminal}/inspections/photos/{photoId}");
        Assert.Equal("image/png", photo.Content.Headers.ContentType!.MediaType);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"{Terminal}/inspections/photos/{photoId}")).StatusCode);

        // Out: a new hole on the left side.
        Assert.Equal(HttpStatusCode.OK, (await planner.PostAsJsonAsync($"{Gate}/out", new { laneCode = "OUT-1", appointmentNo = booked.AppointmentNo })).StatusCode);
        var outSaved = await planner.PostAsJsonAsync($"{path}/inspections", new
        {
            stage = "Out", containerNo = "MSCU1234566", sealSeen = "ML449121",
            damage = new object[]
            {
                new { side = "Door", kind = "Dent", severity = "Minor" },
                new { side = "Left", kind = "Hole", severity = "Major" },
            },
        });
        Assert.Equal(HttpStatusCode.OK, outSaved.StatusCode);
        var gateOut = (await Read<AdminResponse<InspectionView>>(outSaved)).Data;
        Assert.Equal(2, gateOut.Damage.Count);
        Assert.Equal(DamageSide.Left, Assert.Single((await Read<AppointmentInspections>(await dispatcher.GetAsync($"{path}/inspections"))).NewAtGateOut).Side);

        // The company disputes the gate-out inspection; staff can't open one, nor twice.
        Assert.Equal(HttpStatusCode.Forbidden, (await planner.PostAsJsonAsync($"{Terminal}/inspections/{gateOut.InspectionId}/dispute", new { reason = "Not ours" })).StatusCode);
        var opened = await dispatcher.PostAsJsonAsync($"{Terminal}/inspections/{gateOut.InspectionId}/dispute", new { reason = "The hole was made by the terminal's reach stacker." });
        Assert.Equal(HttpStatusCode.Created, opened.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await dispatcher.PostAsJsonAsync($"{Terminal}/inspections/{gateOut.InspectionId}/dispute", new { reason = "Again, please" })).StatusCode);

        var disputed = Assert.Single(await Read<List<InspectionRow>>(await planner.GetAsync($"{Terminal}/inspections?disputedOnly=true")));
        Assert.Equal(DisputeStatus.Open, disputed.Dispute);
        Assert.Equal(2, (await Read<List<InspectionRow>>(await dispatcher.GetAsync($"{Terminal}/inspections?damageOnly=true"))).Count);
        Assert.Empty(await Read<List<InspectionRow>>(await other.GetAsync($"{Terminal}/inspections")));

        Assert.Equal(HttpStatusCode.Forbidden, (await dispatcher.PostAsJsonAsync($"{Terminal}/disputes/{disputed.DisputeId}/resolve", new { accept = true, note = "ok" })).StatusCode);
        var resolved = await planner.PostAsJsonAsync($"{Terminal}/disputes/{disputed.DisputeId}/resolve", new { accept = true, note = "Camera footage confirms it; we will repair." });
        Assert.Equal(DisputeStatus.Accepted, (await Read<AdminResponse<InspectionView>>(resolved)).Data.Dispute!.Status);
        Assert.Equal(HttpStatusCode.Conflict, (await planner.PostAsJsonAsync($"{Terminal}/disputes/{disputed.DisputeId}/resolve", new { accept = false, note = "no" })).StatusCode);

        // The gate ticket shows the seal seen and the damage from the inspection.
        var ticket = await Read<GateTicketView>(await planner.GetAsync($"{Gate}/tickets/{booked.AppointmentNo}"));
        Assert.Equal((SealStatus.Mismatch, "ML449121"), (ticket.Moves.Single().SealStatus, ticket.Moves.Single().Seals.Single()));
        Assert.Equal(DamageKind.Dent, Assert.Single(ticket.Damage).Kind);
    }
}
