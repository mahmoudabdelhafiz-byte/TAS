using Yardsyncs.Domain.Booking;

namespace Yardsyncs.Infrastructure.Tos.N4;

/// <summary>
/// Configuration section "N4". One entry per Yardsyncs terminal code, so one deployment can serve terminals on
/// different N4 instances. Passwords belong in environment variables / secrets, e.g. N4__Terminals__JED1__Password.
/// </summary>
public sealed class N4Options
{
    /// <summary>Keyed by Yardsyncs terminal code (case-insensitive).</summary>
    public Dictionary<string, N4TerminalOptions> Terminals { get; set; } = new();

    /// <summary>
    /// N4 hold id → Yardsyncs check, for holds whose names don't say what they are, e.g. { "H12": "CustomsRelease" }.
    /// Values: CustomsRelease, LineRelease, TerminalHold, Payment, CargoRelease.
    /// </summary>
    public Dictionary<string, CheckType> HoldMapping { get; set; } = new();

    public N4TerminalOptions? For(string terminalCode) =>
        Terminals.FirstOrDefault(t => string.Equals(t.Key, terminalCode, StringComparison.OrdinalIgnoreCase)).Value;
}

public sealed class N4TerminalOptions
{
    /// <summary>ArgoService endpoint, e.g. http://n4-server:9080/apex/services/argoservice</summary>
    public string Url { get; set; } = "";

    /// <summary>An N4 user with web-service rights (read-only is enough for the lookup).</summary>
    public string Username { get; set; } = "";

    public string Password { get; set; } = "";

    // Scope coordinates of the facility, as in N4 (operator / complex / facility / yard ids).
    public string OperatorId { get; set; } = "";
    public string ComplexId { get; set; } = "";
    public string FacilityId { get; set; } = "";
    public string YardId { get; set; } = "";

    /// <summary>Name of the Groovy code extension installed in N4 (see integrations/n4/TasLookup.groovy).</summary>
    public string CodeExtension { get; set; } = "TasLookup";

    public int TimeoutSeconds { get; set; } = 15;

    // Gate passages written to N4 (terminal setting tos_gate_write). The N4 user then needs gate rights.
    /// <summary>The N4 gate id trucks pass through, e.g. MAIN_GATE.</summary>
    public string GateId { get; set; } = "GATE";

    public string IngateStage { get; set; } = "ingate";
    public string OutgateStage { get; set; } = "outgate";

    /// <summary>Overrides of the move type → N4 transaction type mapping, e.g. { "BB_DELIVERY": "DC" }.</summary>
    public Dictionary<string, string> TranTypes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Namespaces of the ArgoService WSDL; change only if your N4 version differs.</summary>
    public string ArgoNamespace { get; set; } = "http://www.navis.com/services/argoservice";

    public string TypesNamespace { get; set; } = "http://types.webservice.argo.navis.com/v1.0";
}
