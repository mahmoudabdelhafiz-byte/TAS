using System.Text.Json;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Yardsyncs.Application.Tos;
using Yardsyncs.Application.Tos.N4;
using Yardsyncs.Domain;
using Yardsyncs.Infrastructure.Persistence;
using Yardsyncs.Infrastructure.Tos.N4;

namespace Yardsyncs.Infrastructure.Tests.TosKit;

/// <summary>
/// The Navis N4 adapter against a simulated N4: it reads the TasLookup request from the genericInvoke document and
/// answers from the scenario, as the code extension would. Needs the test database (the adapter reads the terminal).
/// </summary>
public sealed class N4ConformanceTests : TosAdapterConformance
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("TAS_TEST_DB");
    private YardsyncsDbContext? _db;
    private int _terminalId;

    private sealed class Options(N4Options value) : IOptionsMonitor<N4Options>
    {
        public N4Options CurrentValue => value;
        public N4Options Get(string? name) => value;
        public IDisposable? OnChange(Action<N4Options, string?> listener) => null;
    }

    /// <summary>N4 holding the scenario; or, when down, failing as the real client does on a timeout.</summary>
    private sealed class SimulatedN4(TosScenario s, bool down) : IN4Client
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        public Task<string> InvokeAsync(N4TerminalOptions terminal, string xmlDoc, CancellationToken ct)
        {
            if (down)
                throw new TosUnavailableException("N4 did not answer within 15 s.");

            var json = XElement.Parse(xmlDoc).Descendants("parameter").Single(p => (string?)p.Attribute("id") == "request").Attribute("value")!.Value;
            var request = JsonSerializer.Deserialize<N4LookupRequest>(json, Json)!;
            bool Is(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

            var units = request.Units.Select(c =>
                Is(c, s.ReadyImport) ? new N4Unit(c, true, "1", "IMPRT", "FCL", "S40_YARD", Holds: [], Block: "A1", Position: "A1.12.03.2")
                : Is(c, s.CustomsHeldImport) ? new N4Unit(c, true, "2", "IMPRT", "FCL", "S40_YARD", Holds: ["CUSTOMS HOLD"], Block: "B2", Position: "B2.01.01.1")
                : new N4Unit(c, false)).ToList();
            var orders = request.Orders.Select(o =>
                Is(o, s.ExportBooking) ? new N4Order(o, true, "10", "BOOK", "MSC", 5, s.SlotStart.AddDays(2), [])
                : Is(o, s.EmptyRelease) ? new N4Order(o, true, "11", "ERO", "MSC", 3, null, [])
                : new N4Order(o, false)).ToList();
            var bls = request.Bls.Select(b =>
                Is(b, s.ReleasedCargo) ? new N4BillOfLading(b, true, "20", true, [])
                : Is(b, s.UnreleasedCargo) ? new N4BillOfLading(b, true, "21", false, [])
                : new N4BillOfLading(b, false)).ToList();
            var blUnits = (request.BlUnits ?? []).Select(b => Is(b, s.KnownBl)
                ? new N4BlUnits(s.KnownBl, true, [.. s.KnownBlContainers.Select(c => new N4BlUnit(c, "22G1"))])
                : new N4BlUnits(b, false)).ToList();
            return Task.FromResult(JsonSerializer.Serialize(new N4LookupResult(units, orders, bls, blUnits), Json));
        }
    }

    private static readonly N4Options Config = new()
    {
        Terminals = new() { ["KIT"] = new N4TerminalOptions { Url = "http://n4.test", FacilityId = "KIT" } },
    };

    protected override bool Enabled => ConnectionString is not null;

    protected override int TerminalId => _terminalId;

    protected override ITosAdapter Adapter =>
        new N4TosAdapter(_db!, new SimulatedN4(Scenario, down: false), new Options(Config), NullLogger<N4TosAdapter>.Instance);

    protected override ITosAdapter UnreachableAdapter =>
        new N4TosAdapter(_db!, new SimulatedN4(Scenario, down: true), new Options(Config), NullLogger<N4TosAdapter>.Instance);

    public override async Task InitializeAsync()
    {
        if (ConnectionString is null)
            return;
        _db = new YardsyncsDbContext(new DbContextOptionsBuilder<YardsyncsDbContext>().UseNpgsql(ConnectionString).Options);
        await _db.Database.ExecuteSqlRawAsync("TRUNCATE terminal, company, app_user RESTART IDENTITY CASCADE");
        var terminal = new Terminal { Code = "KIT", Name = "TOS kit terminal", TerminalType = TerminalType.Container };
        _db.Terminals.Add(terminal);
        await _db.SaveChangesAsync();
        _terminalId = terminal.TerminalId;
    }

    public override async Task DisposeAsync()
    {
        if (_db is not null)
            await _db.DisposeAsync();
    }
}
