using System.Net;
using System.Net.Http.Json;
using Yardsyncs.Api;
using Yardsyncs.Application.Booking;
using Yardsyncs.Application.Gate;
using Yardsyncs.Application.Tos;
using Yardsyncs.Domain.Gate;
using Yardsyncs.Infrastructure;
using Yardsyncs.Infrastructure.Gate;
using Yardsyncs.Infrastructure.Tos;

namespace Yardsyncs.Api.Tests;

/// <summary>Gate passages recorded in the TOS (demo TOS) and the gate ticket.</summary>
public sealed class TosGateApiTests(YardsyncsApiFactory factory) : ApiTestBase(factory)
{
    private string Gate => $"{Terminal}/gate";

    private async Task<AppointmentDto> Book(string container, int truck)
    {
        var dispatcher = await As(DemoDataSeeder.DispatcherEmail);
        var booked = await dispatcher.PostAsJsonAsync(Appointments, new
        {
            slotId = SlotId, companyId = Demo.CompanyId, truckId = Demo.TruckIds[truck],
            items = new[] { new { category = "IMP_PICKUP", containerNo = container } },
        });
        Assert.Equal(HttpStatusCode.Created, booked.StatusCode);
        return await Read<AppointmentDto>(booked);
    }

    /// <summary>Checked at IN-1 (early: tomorrow's slot), then let in from the trouble desk: gated in.</summary>
    private async Task LetIn(HttpClient clerk, string appointmentNo)
    {
        var arrival = (await Read<AdminResponse<ArrivalResult>>(await clerk.PostAsJsonAsync($"{Gate}/arrivals", new { laneCode = "IN-1", appointmentNo }))).Data;
        Assert.Equal(HttpStatusCode.OK, (await clerk.PostAsJsonAsync($"{Gate}/troubles/{arrival.TroubleId}/resolve",
            new { action = "Admit", note = "Planner agreed", appointmentNo })).StatusCode);
    }

    private async Task<List<TosGateMessageView>> Records(HttpClient client) =>
        await Read<List<TosGateMessageView>>(await client.GetAsync($"{Gate}/tos-records"));

    [Fact]
    public async Task Gate_in_and_out_are_recorded_in_the_TOS_and_on_the_gate_ticket()
    {
        if (!Enabled) return;
        var admin = await As(DemoDataSeeder.TerminalAdminEmail);
        var planner = await As(DemoDataSeeder.PlannerEmail);

        // Off by default: nothing is queued.
        var first = await Book("MSCU1234566", 0);
        await LetIn(admin, first.AppointmentNo);
        Assert.Empty(await Records(admin));

        // On: gate-in queued with the passage, sent by the worker (here: "send now").
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"{Terminal}/settings", new { values = new { tos_gate_write = 1 } })).StatusCode);
        var booked = await Book("MEDU2601003", 1);
        await LetIn(admin, booked.AppointmentNo);
        var queued = Assert.Single(await Records(planner));
        Assert.Equal((TosGateStage.GateIn, Yardsyncs.Domain.Gate.TosMessageStatus.Queued, "IN-1"), (queued.Stage, queued.Status, queued.LaneCode));

        Assert.Equal(HttpStatusCode.Forbidden, (await planner.PostAsync($"{Gate}/tos-records/send", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"{Gate}/tos-records/send", null)).StatusCode);
        var sent = Assert.Single(await Records(admin));
        Assert.Equal((Yardsyncs.Domain.Gate.TosMessageStatus.Sent, $"TV-{booked.AppointmentNo}"), (sent.Status, sent.TosReference));
        var record = FakeTosAdapter.GateRecords.Last(r => r.Request.AppointmentNo == booked.AppointmentNo).Request;
        Assert.Equal(("IMP_PICKUP", "MEDU2601003", "5678 HDR"), (record.Moves.Single().CategoryCode, record.Moves.Single().ContainerNo, record.TruckPlate));

        // Out: recorded against the TOS's visit.
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"{Gate}/out", new { laneCode = "OUT-1", appointmentNo = booked.AppointmentNo })).StatusCode);
        await admin.PostAsync($"{Gate}/tos-records/send", null);
        var gateOut = FakeTosAdapter.GateRecords.Last(r => r.Request.AppointmentNo == booked.AppointmentNo).Request;
        Assert.Equal((TosGateStage.GateOut, $"TV-{booked.AppointmentNo}", "OUT-1"), (gateOut.Stage, gateOut.VisitReference, gateOut.LaneCode));
        Assert.All(await Records(admin), r => Assert.Equal(Yardsyncs.Domain.Gate.TosMessageStatus.Sent, r.Status));

        var ticket = await Read<GateTicketView>(await planner.GetAsync($"{Gate}/tickets/{booked.AppointmentNo.ToLowerInvariant()}"));
        Assert.Equal(($"TV-{booked.AppointmentNo}", "IN-1", "OUT-1", "5678 HDR"), (ticket.TosVisitReference, ticket.InLane, ticket.OutLane, ticket.TruckPlate));
        Assert.NotNull(ticket.GateInAt);
        Assert.NotNull(ticket.GateOutAt);
        Assert.Equal("MEDU2601003", Assert.Single(ticket.Moves).ContainerNo);
        Assert.Equal(HttpStatusCode.NotFound, (await planner.GetAsync($"{Gate}/tickets/NOPE-1")).StatusCode);
    }

    [Fact]
    public async Task A_record_the_TOS_refuses_waits_for_a_person_and_holds_back_the_gate_out()
    {
        if (!Enabled) return;
        var admin = await As(DemoDataSeeder.TerminalAdminEmail);
        await admin.PutAsJsonAsync($"{Terminal}/settings", new { values = new { tos_gate_write = 1 } });
        var booked = await Book("MSCU5523417", 0);
        await LetIn(admin, booked.AppointmentNo);
        await admin.PostAsync($"{Gate}/tos-records/send", null);

        var failed = Assert.Single(await Read<List<TosGateMessageView>>(await admin.GetAsync($"{Gate}/tos-records?problemsOnly=true")));
        Assert.Equal(Yardsyncs.Domain.Gate.TosMessageStatus.Failed, failed.Status);
        Assert.Contains("refuses MSCU5523417", failed.LastError);

        // Sending it again doesn't change the TOS's mind.
        var retried = await admin.PostAsync($"{Gate}/tos-records/{failed.MessageId}/retry", null);
        Assert.Equal(Yardsyncs.Domain.Gate.TosMessageStatus.Failed, (await Read<AdminResponse<TosGateMessageView>>(retried)).Data.Status);

        // The truck still leaves; its gate-out is not sent without the gate-in.
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"{Gate}/out", new { laneCode = "OUT-1", appointmentNo = booked.AppointmentNo })).StatusCode);
        await admin.PostAsync($"{Gate}/tos-records/send", null);
        var gateOut = (await Records(admin)).Single(r => r.Stage == TosGateStage.GateOut);
        Assert.Equal(Yardsyncs.Domain.Gate.TosMessageStatus.Failed, gateOut.Status);
        Assert.Contains("gate-in", gateOut.LastError);
    }
}
