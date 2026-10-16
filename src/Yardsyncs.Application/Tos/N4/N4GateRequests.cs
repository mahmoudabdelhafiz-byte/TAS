using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Yardsyncs.Application.Tos.N4;

/// <summary>Where the N4 gate is and how Yardsyncs move types map to N4 transaction types (per terminal).</summary>
/// <param name="GateId">The N4 gate id, e.g. MAIN_GATE.</param>
/// <param name="TranTypes">Yardsyncs move type → N4 truck transaction type (DI deliver import, RE receive export, RM receive empty, DM deliver empty…).</param>
public sealed record N4GateSetup(string GateId, string IngateStage, string OutgateStage, IReadOnlyDictionary<string, string> TranTypes)
{
    public static readonly IReadOnlyDictionary<string, string> DefaultTranTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["IMP_PICKUP"] = "DI",
        ["EXP_DROP"] = "RE",
        ["MTY_RETURN"] = "RM",
        ["MTY_PICKUP"] = "DM",
        ["BB_DELIVERY"] = "DB",
        ["BULK_DELIVERY"] = "DB",
    };
}

/// <summary>
/// The N4 gate web-service documents (the &lt;gate&gt; requests sent through ArgoService genericInvoke), built from a
/// gate passage, and the reading of N4's answer.
/// VERIFY against the customer's N4 test system before turning tos_gate_write on: the element and attribute names
/// follow the N4 Gate API (process-truck at a stage, truck-visit by tv-key); N4 versions and gate configurations differ.
/// </summary>
public static class N4GateRequests
{
    /// <summary>Gate-in: the truck, its driver and company, and one truck transaction per move, processed at the in-gate stage.</summary>
    public static string GateIn(TosGateRequest r, N4GateSetup gate)
    {
        var transactions = r.Moves.Select(m =>
        {
            var tran = new XElement("truck-transaction",
                new XAttribute("tran-type", gate.TranTypes.TryGetValue(m.CategoryCode, out var type) ? type : m.CategoryCode),
                new XAttribute("appt-nbr", r.AppointmentNo));
            if (m.ContainerNo is not null)
                tran.Add(new XElement("container", new XAttribute("eqid", m.ContainerNo), m.IsoType is null ? null : new XAttribute("type", m.IsoType)));
            if ((m.BookingRef ?? m.EdoRef) is { } order)
                tran.Add(new XElement("eq-order", new XAttribute("order-nbr", order)));
            if (m.CargoRef is not null)
                tran.Add(new XElement("bl-nbr", m.CargoRef));
            if (m.Seals.Count > 0)
                tran.Add(new XElement("seals", m.Seals.Take(4).Select((s, i) => new XAttribute($"seal-{i + 1}", s))));
            return tran;
        });

        return new XElement("gate",
            new XElement("process-truck",
                new XElement("gate-id", gate.GateId),
                new XElement("stage-id", gate.IngateStage),
                r.LaneCode is null ? null : new XElement("lane-id", r.LaneCode),
                new XElement("truck", new XAttribute("license-nbr", r.TruckPlate ?? "")),
                new XElement("truck-visit",
                    new XAttribute("appt-nbr", r.AppointmentNo),
                    new XAttribute("trucking-co", r.CompanyName),
                    r.DriverName is null ? null : new XAttribute("driver-name", r.DriverName)),
                new XElement("truck-transactions", transactions),
                new XElement("timestamp", r.At.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"))))
            .ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>Gate-out: the truck visit (by N4's key from the gate-in) processed at the out-gate stage.</summary>
    public static string GateOut(TosGateRequest r, N4GateSetup gate) =>
        new XElement("gate",
            new XElement("process-truck",
                new XElement("gate-id", gate.GateId),
                new XElement("stage-id", gate.OutgateStage),
                r.LaneCode is null ? null : new XElement("lane-id", r.LaneCode),
                new XElement("truck", new XAttribute("license-nbr", r.TruckPlate ?? "")),
                new XElement("truck-visit",
                    r.VisitReference is null ? new XAttribute("appt-nbr", r.AppointmentNo) : new XAttribute("tv-key", r.VisitReference)),
                new XElement("timestamp", r.At.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"))))
            .ToString(SaveOptions.DisableFormatting);

    /// <summary>
    /// N4's answer: messages with severity SEVERE or FATAL mean the gate refused the truck (sending again won't help);
    /// otherwise the truck visit key (tv-key) is the reference for the gate-out.
    /// </summary>
    public static TosGateResult Read(string payload)
    {
        XElement root;
        try
        {
            root = XElement.Parse(payload);
        }
        catch (System.Xml.XmlException)
        {
            return TosGateResult.Unreachable("N4 answered with something that is not XML.");
        }

        var errors = root.Descendants().Where(e => e.Name.LocalName == "message"
                && (string?)e.Attribute("severity") is { } sev && (sev.Equals("SEVERE", StringComparison.OrdinalIgnoreCase) || sev.Equals("FATAL", StringComparison.OrdinalIgnoreCase)))
            .Select(e => ((string?)e.Attribute("message-text") ?? (string?)e.Attribute("text") ?? e.Value).Trim())
            .Where(m => m.Length > 0)
            .ToList();
        if (errors.Count > 0)
            return TosGateResult.Refused(Clip("N4: " + string.Join(" ", errors)));

        var key = root.DescendantsAndSelf().Where(e => e.Name.LocalName == "truck-visit")
            .Select(e => (string?)e.Attribute("tv-key") ?? (string?)e.Attribute("gkey"))
            .FirstOrDefault(k => !string.IsNullOrWhiteSpace(k))
            ?? Regex.Match(payload, "tv-key=\"([^\"]+)\"").Groups[1].Value;
        return TosGateResult.Done(string.IsNullOrWhiteSpace(key) ? null : key);
    }

    private static string Clip(string text) => text.Length <= 400 ? text : text[..399] + "…";
}
