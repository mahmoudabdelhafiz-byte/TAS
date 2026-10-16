using System.Text.RegularExpressions;
using Yardsyncs.Application.Languages;
using Yardsyncs.Domain.Booking;

namespace Yardsyncs.Application.MasterData;

/// <summary>Shape checks for master data. Pure: uniqueness and existence are checked against the database by the services.</summary>
public static partial class MasterDataValidator
{
    /// <summary>Languages people can get messages in: the built-in ones and the installation's language packs.</summary>
    public static IReadOnlySet<string> Languages => LanguageCatalog.All.Where(l => l.Messages).Select(l => l.Code).ToHashSet();

    /// <summary>Languages portal users can choose: those of the portal or of the messages (their emails).</summary>
    public static IReadOnlySet<string> UserLanguages => LanguageCatalog.All.Where(l => l.Messages || l.Portal).Select(l => l.Code).ToHashSet();

    private static string LanguageError => $"Language must be one of: {string.Join(", ", Languages)}.";

    [GeneratedRegex("^[A-Z][A-Z0-9_]{1,29}$")]
    private static partial Regex CategoryCode();

    [GeneratedRegex("^[0-9]{7,15}$")]
    private static partial Regex CrNumber();

    [GeneratedRegex("^[A-Za-z0-9]{5,30}$")]
    private static partial Regex IdNumber();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9/_.-]{1,49}$")]
    private static partial Regex CargoRef();

    public static IReadOnlyList<string> Company(CompanyInput input)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Trim().Length > 200)
            errors.Add("Company name is required (max 200 characters).");
        if (!string.IsNullOrWhiteSpace(input.CrNumber) && !CrNumber().IsMatch(input.CrNumber.Trim()))
            errors.Add("Commercial registration number must be 7–15 digits.");
        if (!Enum.IsDefined(input.CompanyType))
            errors.Add("Unknown company type.");
        return errors;
    }

    public static (string? Normalized, string? Error) Truck(TruckInput input)
    {
        var raw = input.PlateNumber?.Trim() ?? "";
        if (raw.Length is 0 or > 20)
            return (null, "Plate number is required (max 20 characters).");
        var normalized = PlateNumber.Normalize(raw);
        return PlateNumber.IsValid(normalized)
            ? (normalized, null)
            : (null, $"'{raw}' is not a valid plate number.");
    }

    public static (string? Mobile, IReadOnlyList<string> Errors) Driver(DriverInput input)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(input.FullName) || input.FullName.Trim().Length > 150)
            errors.Add("Driver name is required (max 150 characters).");
        if (!IdNumber().IsMatch(input.IdNumber?.Trim() ?? ""))
            errors.Add("ID / Iqama number must be 5–30 letters or digits.");
        var mobile = PhoneNumber.Normalize(input.Mobile);
        if (mobile is null)
            errors.Add($"'{input.Mobile}' is not a valid mobile number.");
        if (!Languages.Contains((input.PreferredLanguage ?? "").Trim().ToLowerInvariant()))
            errors.Add(LanguageError);
        return (mobile, errors);
    }

    public static IReadOnlyList<string> Category(CategoryInput input)
    {
        var errors = new List<string>();
        if (!CategoryCode().IsMatch(input.Code?.Trim().ToUpperInvariant() ?? ""))
            errors.Add("Code must be 2–30 characters: letters, digits and _ (e.g. IMP_PICKUP).");
        if (string.IsNullOrWhiteSpace(input.NameEn) || input.NameEn.Trim().Length > 100)
            errors.Add("English name is required (max 100 characters).");
        if (string.IsNullOrWhiteSpace(input.NameAr) || input.NameAr.Trim().Length > 100)
            errors.Add("Arabic name is required (max 100 characters).");
        if (!Enum.IsDefined(input.Mode) || !Enum.IsDefined(input.UnitType))
            errors.Add("Unknown mode or unit type.");
        return errors;
    }

    public static IReadOnlyList<string> Cargo(CargoInput input)
    {
        var errors = new List<string>();
        if (!CargoRef().IsMatch(input.CargoRef?.Trim() ?? ""))
            errors.Add("Cargo reference (BL / DO) is required: 2–50 letters, digits, - / _ .");
        if (input.TotalTonnage is <= 0)
            errors.Add("Total tonnage must be positive.");
        if (input.TotalTrips is <= 0)
            errors.Add("Total trips must be positive.");
        if (input.TotalTonnage is null && input.TotalTrips is null)
            errors.Add("Give total trips, total tonnage, or both.");
        if (input.Commodity is { Length: > 150 })
            errors.Add("Commodity is at most 150 characters.");
        if (!Enum.IsDefined(input.CargoType) || !Enum.IsDefined(input.Status))
            errors.Add("Unknown cargo type or status.");
        return errors;
    }

    public static IReadOnlyList<string> User(UpdateUserInput input)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(input.FullName) || input.FullName.Trim().Length > 150)
            errors.Add("Full name is required (max 150 characters).");
        if (!string.IsNullOrWhiteSpace(input.Mobile) && PhoneNumber.Normalize(input.Mobile) is null)
            errors.Add($"'{input.Mobile}' is not a valid mobile number.");
        if (!UserLanguages.Contains((input.PreferredLanguage ?? "").Trim().ToLowerInvariant()))
            errors.Add($"Language must be one of: {string.Join(", ", UserLanguages)}.");
        return errors;
    }
}
