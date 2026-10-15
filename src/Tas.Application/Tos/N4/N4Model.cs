using System.Text.Json.Serialization;

namespace Tas.Application.Tos.N4;

/// <summary>What TAS asks N4 for in one call (the TasLookup Groovy code extension answers it).</summary>
public sealed record N4LookupRequest(
    [property: JsonPropertyName("units")] IReadOnlyList<string> Units,
    [property: JsonPropertyName("orders")] IReadOnlyList<string> Orders,
    [property: JsonPropertyName("bls")] IReadOnlyList<string> Bls,
    [property: JsonPropertyName("blUnits"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? BlUnits = null);

/// <summary>A container as N4 sees it now.</summary>
/// <param name="Category">N4 unit category key: IMPRT, EXPRT, STRGE, TRSHP, THRGH.</param>
/// <param name="FreightKind">FCL, LCL, MTY, BBK.</param>
/// <param name="TransitState">UFV transit state key: S10_ADVISED, S20_INBOUND, S30_ECIN, S40_YARD, S50_ECOUT, S60_LOADED, S70_DEPARTED, S99_RETIRED.</param>
/// <param name="Holds">Active road holds / impediments (hold ids as configured in N4).</param>
public sealed record N4Unit(
    [property: JsonPropertyName("containerNo")] string ContainerNo,
    [property: JsonPropertyName("found")] bool Found,
    [property: JsonPropertyName("gkey")] string? Gkey = null,
    [property: JsonPropertyName("category")] string? Category = null,
    [property: JsonPropertyName("freightKind")] string? FreightKind = null,
    [property: JsonPropertyName("transitState")] string? TransitState = null,
    [property: JsonPropertyName("lineOperator")] string? LineOperator = null,
    [property: JsonPropertyName("vgmKg")] decimal? VgmKg = null,
    [property: JsonPropertyName("holds")] IReadOnlyList<string>? Holds = null,
    [property: JsonPropertyName("block")] string? Block = null,
    [property: JsonPropertyName("position")] string? Position = null);

/// <summary>An equipment order — export booking or empty release (EDO) — as N4 sees it now.</summary>
/// <param name="QuantityRemaining">Units still to be received / delivered against the order (null = unknown).</param>
/// <param name="CargoCutoff">Cargo cut-off of the order's vessel visit (exports).</param>
public sealed record N4Order(
    [property: JsonPropertyName("nbr")] string Nbr,
    [property: JsonPropertyName("found")] bool Found,
    [property: JsonPropertyName("gkey")] string? Gkey = null,
    [property: JsonPropertyName("subType")] string? SubType = null,
    [property: JsonPropertyName("lineOperator")] string? LineOperator = null,
    [property: JsonPropertyName("quantityRemaining")] int? QuantityRemaining = null,
    [property: JsonPropertyName("cargoCutoff")] DateTimeOffset? CargoCutoff = null,
    [property: JsonPropertyName("holds")] IReadOnlyList<string>? Holds = null);

/// <summary>A bill of lading (multipurpose / breakbulk cargo) as N4 sees it now.</summary>
public sealed record N4BillOfLading(
    [property: JsonPropertyName("nbr")] string Nbr,
    [property: JsonPropertyName("found")] bool Found,
    [property: JsonPropertyName("gkey")] string? Gkey = null,
    [property: JsonPropertyName("released")] bool? Released = null,
    [property: JsonPropertyName("holds")] IReadOnlyList<string>? Holds = null);

/// <summary>The containers of an import bill of lading (asked for with "blUnits").</summary>
public sealed record N4BlUnits(
    [property: JsonPropertyName("nbr")] string Nbr,
    [property: JsonPropertyName("found")] bool Found,
    [property: JsonPropertyName("units")] IReadOnlyList<N4BlUnit>? Units = null);

public sealed record N4BlUnit(
    [property: JsonPropertyName("containerNo")] string ContainerNo,
    [property: JsonPropertyName("isoType")] string? IsoType = null);

public sealed record N4LookupResult(
    [property: JsonPropertyName("units")] IReadOnlyList<N4Unit>? Units,
    [property: JsonPropertyName("orders")] IReadOnlyList<N4Order>? Orders,
    [property: JsonPropertyName("bls")] IReadOnlyList<N4BillOfLading>? Bls,
    [property: JsonPropertyName("blUnits")] IReadOnlyList<N4BlUnits>? BlUnits = null);
