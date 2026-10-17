using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Yardsyncs.Application.Gate;
using Yardsyncs.Application.Reports;
using Yardsyncs.Domain.Gate;
using Yardsyncs.Infrastructure;
using Yardsyncs.Infrastructure.Persistence;

namespace Yardsyncs.Api.Tests;

/// <summary>Lane KPIs from the lane controller's transactions.</summary>
public sealed class LaneKpiApiTests(YardsyncsApiFactory factory) : ApiTestBase(factory)
{
    [Fact]
    public async Task Terminal_staff_see_how_each_lane_handles_trucks()
    {
        if (!Enabled) return;
        var planner = await As(DemoDataSeeder.PlannerEmail);
        var lanes = await Read<List<LaneView>>(await planner.GetAsync($"{Terminal}/gate/lanes"));
        var in1 = lanes.Single(l => l.LaneCode == "IN-1");
        var start = DateTimeOffset.UtcNow.AddHours(-1);
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<YardsyncsDbContext>();
            LaneTransaction Tx(LaneTransactionStatus status, int decide, int? through, string? reason, bool plateCorrected = false) => new()
            {
                TerminalId = Demo.TerminalId, LaneId = in1.LaneId, Status = status, StartedAt = start, LastReadAt = start,
                DecidedAt = start.AddSeconds(decide), ClosedAt = through is { } s ? start.AddSeconds(s) : null,
                ReadCount = 1, Plate = "1234ABJ", PlateCorrected = plateCorrected, Reason = reason,
            };
            db.LaneTransactions.AddRange(
                Tx(LaneTransactionStatus.Passed, 10, 40, null),
                Tx(LaneTransactionStatus.Passed, 12, 300, "EARLY", plateCorrected: true),
                Tx(LaneTransactionStatus.Referred, 15, null, "NO_APPOINTMENT"));
            await db.SaveChangesAsync();
        }

        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Riyadh).DateTime);
        var url = $"{Terminal}/gate/lane-kpis?from={today.AddDays(-1):yyyy-MM-dd}&to={today:yyyy-MM-dd}";
        var report = await Read<LaneKpiReport>(await planner.GetAsync(url));
        var lane = report.Lanes.Single(l => l.LaneCode == "IN-1").Kpis;
        Assert.Equal(3, lane.Trucks);
        Assert.Equal(0.333, lane.StraightThroughRate);
        Assert.Equal(0.667, lane.ExceptionRate);
        Assert.Equal(12, lane.MedianDecisionSeconds);
        Assert.Equal(40, lane.MedianLaneSeconds);
        Assert.Equal(0.667, lane.PlateReadRate);        // IN-1 has a plate camera; one plate corrected
        Assert.Equal(2, report.Reasons.Count);
        Assert.Equal(2, report.Days.Count);

        var dispatcher = await As(DemoDataSeeder.DispatcherEmail);
        Assert.Equal(HttpStatusCode.Forbidden, (await dispatcher.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await planner.GetAsync($"{Terminal}/gate/lane-kpis?from={today:yyyy-MM-dd}&to={today.AddDays(-1):yyyy-MM-dd}")).StatusCode);
    }
}
