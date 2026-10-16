namespace Yardsyncs.Domain;

public enum CategoryMode
{
    /// <summary>Booked by container number.</summary>
    Container,

    /// <summary>Booked by BL / cargo reference (breakbulk, bulk, RoRo, project cargo).</summary>
    Cargo,
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
}
