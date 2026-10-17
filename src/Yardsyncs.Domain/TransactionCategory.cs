namespace Yardsyncs.Domain;

public enum CategoryMode
{
    /// <summary>Booked by container number.</summary>
    Container,

    /// <summary>Booked by BL / cargo reference (breakbulk, bulk, RoRo, project cargo).</summary>
    Cargo,
}

/// <summary>Which way a move's cargo goes through the gate.</summary>
public enum CategoryFlow
{
    /// <summary>The truck brings it into the terminal (export drop-off, empty return): it leaves without it.</summary>
    In,

    /// <summary>The truck takes it out of the terminal (import pickup, empty pickup, cargo delivery): it leaves with it.</summary>
    Out,
}

public enum UnitType
{
    Move,
    Trip,
}

/// <summary>A transaction type that has its own capacity quota, e.g. IMP_PICKUP or EXP_DROP.</summary>
public sealed class TransactionCategory
{
    public int CategoryId { get; set; }
    public int TerminalId { get; set; }
    public string Code { get; set; } = "";
    public string NameEn { get; set; } = "";
    public string NameAr { get; set; } = "";
    public CategoryMode Mode { get; set; }
    public UnitType UnitType { get; set; } = UnitType.Move;
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>Which way the cargo goes, for the exit check; null = not checked.</summary>
    public CategoryFlow? Flow { get; set; }

    /// <summary>The usual direction for a move type code: …PICKUP / …DELIVERY go out, …DROP / …RETURN / …RECEIV… come in.</summary>
    public static CategoryFlow? GuessFlow(string code)
    {
        var c = code.ToUpperInvariant();
        if (c.Contains("PICKUP") || c.Contains("DELIVERY")) return CategoryFlow.Out;
        if (c.Contains("DROP") || c.Contains("RETURN") || c.Contains("RECEIV")) return CategoryFlow.In;
        return null;
    }
}
