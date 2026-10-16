using Microsoft.Extensions.Logging.Abstractions;
using Tas.Application.Tos;
using Tas.Domain;
using Tas.Domain.Booking;
using Tas.Infrastructure.Tos;

namespace Tas.Infrastructure.Tests.TosKit;

/// <summary>Yardsyncs holds every live answer to the contract: a careless adapter can't let a held container through.</summary>
public class TosContractGuardTests
{
    private sealed class Careless : ITosAdapter
    {
        public IReadOnlyList<TosCheck> Checks { get; set; } = [];
        public IReadOnlyList<TosBlUnit>? Bl { get; set; }
        public IReadOnlyList<TosYardPosition> Positions { get; set; } = [];
        public string Source => "CARELESS";
        public Task<IReadOnlyList<TosCheck>> ValidateAsync(int terminalId, IReadOnlyList<TosItemQuery> items, CancellationToken ct = default) => Task.FromResult(Checks);
        public Task<IReadOnlyList<TosBlUnit>?> FindBlContainersAsync(int terminalId, string blNo, CancellationToken ct = default) => Task.FromResult(Bl);
        public Task<IReadOnlyList<TosYardPosition>> YardPositionsAsync(int terminalId, IReadOnlyList<TosItemQuery> items, CancellationToken ct = default) => Task.FromResult(Positions);
    }

    private static readonly IReadOnlyList<TosItemQuery> Import =
        [new(0, "IMP_PICKUP", CategoryMode.Container, "MSCU7654329", null, null, null, DateTimeOffset.UtcNow)];

    [Fact]
    public async Task An_answer_without_the_customs_check_is_not_a_pass()
    {
        var careless = new Careless { Checks = [new TosCheck(0, CheckType.Availability, CheckResult.Pass, null)] };
        var guarded = new TosContractGuard(careless, NullLogger.Instance);
        var error = await Assert.ThrowsAsync<TosUnavailableException>(() => guarded.ValidateAsync(1, Import));
        Assert.Contains("no CustomsRelease", error.Message);

        careless.Checks = [.. careless.Checks, .. TosContract.ImportReleaseChecks.Select(t => new TosCheck(0, t, CheckResult.Pass, null))];
        Assert.Equal(5, (await guarded.ValidateAsync(1, Import)).Count);
        Assert.Equal("CARELESS", guarded.Source);
    }

    [Fact]
    public async Task An_unusable_bl_list_is_refused_and_bad_positions_left_out()
    {
        var careless = new Careless
        {
            Bl = [new TosBlUnit("mscu1234566 ", null)],
            Positions = [new TosYardPosition(0, "B2", "14.03.2", null), new TosYardPosition(0, "B3", null, null), new TosYardPosition(9, "C1", null, null)],
        };
        var guarded = new TosContractGuard(careless, NullLogger.Instance);
        await Assert.ThrowsAsync<TosUnavailableException>(() => guarded.FindBlContainersAsync(1, "BL1"));
        Assert.Equal("B2", Assert.Single(await guarded.YardPositionsAsync(1, Import)).Block);

        careless.Bl = null; // an unknown BL is a fine answer
        Assert.Null(await guarded.FindBlContainersAsync(1, "BL1"));
    }
}
