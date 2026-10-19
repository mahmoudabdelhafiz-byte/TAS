using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Yardsyncs.Application.Tos;
using Yardsyncs.Application.Zone;
using Yardsyncs.Domain;
using Yardsyncs.Domain.Booking;
using Yardsyncs.Infrastructure.Zone;

namespace Yardsyncs.Infrastructure.Tests;

public class NafithTests
{
    private static readonly DateTimeOffset Slot = new(2026, 10, 21, 10, 0, 0, TimeSpan.FromHours(2));

    [Theory]
    [InlineData("""{"status":"CLEARED","reference":"ACI-1"}""", ZoneClearanceState.Cleared)]
    [InlineData("""{"status":"not cleared","message":"Inspection"}""", ZoneClearanceState.NotCleared)]
    [InlineData("""{"state":"ON_HOLD"}""", ZoneClearanceState.NotCleared)]
    [InlineData("""{"cleared":true}""", ZoneClearanceState.Cleared)]
    [InlineData("""{"cleared":false}""", ZoneClearanceState.NotCleared)]
    [InlineData("""{"status":"SOMETHING"}""", ZoneClearanceState.Unknown)]
    [InlineData("""[1,2]""", ZoneClearanceState.Unknown)]
    public void Nafiths_clearance_answers_are_read(string json, ZoneClearanceState expected) =>
        Assert.Equal(expected, NafithZoneSystem.ReadClearance("MSCU1234566", JsonNode.Parse(json)).State);

    [Fact]
    public void Nafiths_permit_answers_are_read()
    {
        var valid = NafithZoneSystem.ReadPermit(JsonNode.Parse("""{"valid":true,"permitNo":"NP-9","validUntil":"2026-10-21T18:00:00Z"}"""));
        Assert.True(valid.Valid);
        Assert.Equal("NP-9", valid.PermitNo);
        Assert.Equal(new DateTimeOffset(2026, 10, 21, 18, 0, 0, TimeSpan.Zero), valid.ValidUntil);

        var expired = NafithZoneSystem.ReadPermit(JsonNode.Parse("""{"valid":false,"reason":"Permit expired"}"""));
        Assert.Equal((false, "Permit expired"), (expired.Valid, expired.Problem));
        Assert.Equal((false, "not found"), (NafithZoneSystem.ReadPermit(null).Valid, NafithZoneSystem.ReadPermit(null).Problem));
    }

    private static NafithZoneSystem Simulator(params string[] terminals) => new(new HttpClient(), Options.Create(new NafithOptions
    {
        Mode = "Simulator", Terminals = [.. terminals], SimulatorNotCleared = ["CAIU5522102"], SimulatorNoPermit = ["9021 KHB"],
    }));

    [Fact]
    public async Task The_simulator_answers_without_a_network()
    {
        var nafith = Simulator("DEMO");
        Assert.True(nafith.CoversTerminal("demo"));
        Assert.False(nafith.CoversTerminal("JED1"));
        Assert.False(new NafithZoneSystem(new HttpClient(), Options.Create(new NafithOptions())).CoversTerminal("DEMO")); // Off by default

        var clearance = await nafith.ClearanceAsync("DEMO", ["MSCU1234566", "CAIU5522102"], default);
        Assert.Equal(new[] { ZoneClearanceState.Cleared, ZoneClearanceState.NotCleared }, clearance.Select(c => c.State));
        Assert.False((await nafith.PermitAsync("DEMO", new ZoneTruck("9021KHB", null, null), Slot, default)).Valid);
        Assert.True((await nafith.PermitAsync("DEMO", new ZoneTruck("1234ABJ", "1012345678", null), Slot, default)).Valid);
    }

    private sealed class PassingTos : ITosAdapter
    {
        public string Source => "N4";

        public Task<IReadOnlyList<TosCheck>> ValidateAsync(int terminalId, IReadOnlyList<TosItemQuery> items, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TosCheck>>(items.Select(i => new TosCheck(i.Index, CheckType.Availability, CheckResult.Pass, null)).ToList());

        public Task<IReadOnlyList<TosBlUnit>?> FindBlContainersAsync(int terminalId, string blNo, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TosBlUnit>?>(null);

        public Task<IReadOnlyList<TosYardPosition>> YardPositionsAsync(int terminalId, IReadOnlyList<TosItemQuery> items, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TosYardPosition>>([]);
    }

    private sealed class DownZone : IZoneSystem
    {
        public string Name => "NAFITH";
        public bool CoversTerminal(string terminalCode) => true;
        public Task<IReadOnlyList<ZoneClearance>> ClearanceAsync(string terminalCode, IReadOnlyList<string> containers, CancellationToken ct) =>
            throw new ZoneUnavailableException("down");
        public Task<ZonePermit> PermitAsync(string terminalCode, ZoneTruck truck, DateTimeOffset visitAt, CancellationToken ct) =>
            throw new ZoneUnavailableException("down");
    }

    private static readonly TosItemQuery[] Items =
    [
        new(1, "IMP_PICKUP", CategoryMode.Container, "MSCU1234566", null, null, null, Slot),
        new(2, "IMP_PICKUP", CategoryMode.Container, "CAIU5522102", null, null, null, Slot),
    ];

    [Fact]
    public async Task The_zones_clearance_is_added_to_the_TOS_checks_and_a_down_zone_only_warns()
    {
        var checks = await new ZoneTosAdapter(new PassingTos(), Simulator(), "DEMO", NullLogger.Instance).ValidateAsync(1, Items);
        Assert.Equal(4, checks.Count);
        Assert.Equal(CheckResult.Fail, checks.Single(c => c.ItemIndex == 2 && c.Type == CheckType.CustomsRelease).Result);
        Assert.Equal(CheckResult.Pass, checks.Single(c => c.ItemIndex == 1 && c.Type == CheckType.CustomsRelease).Result);

        var down = await new ZoneTosAdapter(new PassingTos(), new DownZone(), "DEMO", NullLogger.Instance).ValidateAsync(1, Items);
        Assert.Equal(2, down.Count(c => c.Type == CheckType.CustomsRelease && c.Result == CheckResult.Warn));
        Assert.DoesNotContain(down, c => c.Result == CheckResult.Fail);
    }
}
