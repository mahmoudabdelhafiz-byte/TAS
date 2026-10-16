using System.Xml.Linq;
using Yardsyncs.Application.Tos;
using Yardsyncs.Application.Tos.N4;

namespace Yardsyncs.Application.Tests;

public class N4GateRequestsTests
{
    private static readonly N4GateSetup Gate = new("MAIN_GATE", "ingate", "outgate", N4GateSetup.DefaultTranTypes);
    private static readonly DateTimeOffset At = new(2026, 10, 20, 10, 5, 0, TimeSpan.FromHours(3));

    private static TosGateRequest Request(TosGateStage stage, string? visit = null) => new(
        stage, "DEMO-261020-00001", "1234 ABJ", "Khalid", "Al Noor Transport", "MAIN", "IN-1", At, visit,
        [
            new TosGateMove(1, "IMP_PICKUP", "Container", "MSCU1234566", "22G1", null, "EDO1", null, ["SL1", "SL2"]),
            new TosGateMove(2, "EXP_DROP", "Container", "CSQU3054383", "45G1", "BKG77", null, null, []),
        ]);

    [Fact]
    public void Gate_in_carries_the_truck_and_one_transaction_per_move()
    {
        var doc = XElement.Parse(N4GateRequests.GateIn(Request(TosGateStage.GateIn), Gate));
        var truck = doc.Element("process-truck")!;
        Assert.Equal(("MAIN_GATE", "ingate", "IN-1"), ((string)truck.Element("gate-id")!, (string)truck.Element("stage-id")!, (string)truck.Element("lane-id")!));
        Assert.Equal("1234 ABJ", (string)truck.Element("truck")!.Attribute("license-nbr")!);
        Assert.Equal("2026-10-20T07:05:00Z", (string)truck.Element("timestamp")!);

        var trans = truck.Element("truck-transactions")!.Elements("truck-transaction").ToList();
        Assert.Equal(new[] { "DI", "RE" }, trans.Select(t => (string)t.Attribute("tran-type")!));
        Assert.Equal("MSCU1234566", (string)trans[0].Element("container")!.Attribute("eqid")!);
        Assert.Equal("EDO1", (string)trans[0].Element("eq-order")!.Attribute("order-nbr")!);
        Assert.Equal("SL2", (string)trans[0].Element("seals")!.Attribute("seal-2")!);
        Assert.Equal("BKG77", (string)trans[1].Element("eq-order")!.Attribute("order-nbr")!);
    }

    [Fact]
    public void Gate_out_names_the_visit_by_N4s_key()
    {
        var doc = XElement.Parse(N4GateRequests.GateOut(Request(TosGateStage.GateOut, "88123"), Gate));
        Assert.Equal("outgate", (string)doc.Element("process-truck")!.Element("stage-id")!);
        Assert.Equal("88123", (string)doc.Element("process-truck")!.Element("truck-visit")!.Attribute("tv-key")!);
        var noKey = XElement.Parse(N4GateRequests.GateOut(Request(TosGateStage.GateOut), Gate));
        Assert.Equal("DEMO-261020-00001", (string)noKey.Element("process-truck")!.Element("truck-visit")!.Attribute("appt-nbr")!);
    }

    [Fact]
    public void N4s_answer_is_read_as_recorded_refused_or_unreachable()
    {
        var ok = N4GateRequests.Read("<gate-response><process-truck-response><truck-visit tv-key=\"88123\" status=\"OK\"/></process-truck-response></gate-response>");
        Assert.Equal((true, "88123"), (ok.Ok, ok.VisitReference));

        var refused = N4GateRequests.Read("<gate-response><messages><message severity=\"SEVERE\" message-text=\"Unit MSCU1234566 has an active hold\"/></messages></gate-response>");
        Assert.False(refused.Ok);
        Assert.False(refused.Retry);
        Assert.Equal("N4: Unit MSCU1234566 has an active hold", refused.Message);

        var info = N4GateRequests.Read("<gate-response><messages><message severity=\"INFO\" message-text=\"Lane assigned\"/></messages><truck-visit gkey=\"5\"/></gate-response>");
        Assert.True(info.Ok);

        var garbage = N4GateRequests.Read("<html>proxy error");
        Assert.True(garbage.Retry);
        Assert.False(garbage.Ok);
    }

    [Fact]
    public void Records_are_retried_ever_more_slowly_up_to_an_hour()
    {
        Assert.Equal(TimeSpan.FromMinutes(1), TosGateRetry.Delay(1));
        Assert.Equal(TimeSpan.FromMinutes(8), TosGateRetry.Delay(4));
        Assert.Equal(TimeSpan.FromMinutes(60), TosGateRetry.Delay(9));
    }
}
