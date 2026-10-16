using System.Globalization;

namespace Tas.Application.Countries;

/// <summary>A public holiday on one day. Lunar ones follow the Umm al-Qura calendar and may move a day with the moon sighting.</summary>
public sealed record Holiday(DateOnly Date, string NameEn, string NameAr, bool Lunar);

/// <summary>Local mobile numbers of a country: what follows the dialling code, and how people type them at home.</summary>
/// <param name="Length">Digits after the dialling code.</param>
/// <param name="Starts">The first digit(s) of a mobile number after the dialling code.</param>
/// <param name="TrunkPrefix">Typed before the number at home ("0" in Saudi Arabia), or "" when none.</param>
public sealed record MobileRule(string DialCode, int Length, IReadOnlyList<string> Starts, string TrunkPrefix, string Example);

/// <summary>
/// What changes from one country to the next: mobile numbers, truck plates, the drivers' ID document, the currency,
/// the usual time zone and the public holidays. The installation's pack (setting <c>Country</c>) reads mobiles and
/// plates, because companies and drivers are shared by every terminal; each terminal can have its own pack for its
/// holidays and currency (<c>Terminals:&lt;code&gt;:Country</c>).
/// </summary>
public sealed class CountryPack
{
    public required string Code { get; init; }
    public required string NameEn { get; init; }
    public required string NameAr { get; init; }
    public required MobileRule Mobile { get; init; }

    /// <summary>Arabic letters on the country's plates and the Latin letter each stands for; empty when plates use Latin letters only.</summary>
    public IReadOnlyDictionary<char, char> PlateLetters { get; init; } = new Dictionary<char, char>();

    public required string IdNameEn { get; init; }
    public required string IdNameAr { get; init; }
    public required string Currency { get; init; }
    public int CurrencyDecimals { get; init; } = 2;
    public required string TimeZone { get; init; }

    internal Func<int, IEnumerable<Holiday>> HolidaysOf { get; init; } = _ => [];

    /// <summary>The public holidays of a Gregorian year, one row per day, in date order.</summary>
    public IReadOnlyList<Holiday> Holidays(int year) =>
        HolidaysOf(year).Where(h => h.Date.Year == year).DistinctBy(h => (h.Date, h.NameEn)).OrderBy(h => h.Date).ToList();
}

/// <summary>The country packs Yardsyncs ships with. A new country is a new entry here, with its tests.</summary>
public static class CountryPacks
{
    private static readonly UmAlQuraCalendar Hijri = new();

    private static readonly Dictionary<char, char> SaudiPlateLetters = new()
    {
        ['ا'] = 'A', ['أ'] = 'A', ['إ'] = 'A', ['آ'] = 'A',
        ['ب'] = 'B', ['ح'] = 'J', ['د'] = 'D', ['ر'] = 'R', ['س'] = 'S', ['ص'] = 'X', ['ط'] = 'T',
        ['ع'] = 'E', ['ق'] = 'G', ['ك'] = 'K', ['ل'] = 'L', ['م'] = 'Z', ['ن'] = 'N',
        ['ه'] = 'H', ['ة'] = 'H', ['و'] = 'U', ['ى'] = 'V', ['ي'] = 'V',
    };

    public static readonly CountryPack SaudiArabia = new()
    {
        Code = "SA", NameEn = "Saudi Arabia", NameAr = "المملكة العربية السعودية",
        Mobile = new("966", 9, ["5"], "0", "05XXXXXXXX"),
        PlateLetters = SaudiPlateLetters,
        IdNameEn = "ID / Iqama number", IdNameAr = "رقم الهوية / الإقامة",
        Currency = "SAR", TimeZone = "Asia/Riyadh",
        HolidaysOf = year => Lunar(year, EidAlFitr(3), ArafatAndEidAlAdha(3))
            .Concat(Fixed(year, (2, 22, "Founding Day", "يوم التأسيس"), (9, 23, "National Day", "اليوم الوطني"))),
    };

    public static readonly CountryPack UnitedArabEmirates = new()
    {
        Code = "AE", NameEn = "United Arab Emirates", NameAr = "الإمارات العربية المتحدة",
        Mobile = new("971", 9, ["5"], "0", "05XXXXXXXX"),
        IdNameEn = "Emirates ID number", IdNameAr = "رقم الهوية الإماراتية",
        Currency = "AED", TimeZone = "Asia/Dubai",
        HolidaysOf = year => Lunar(year, EidAlFitr(3), ArafatAndEidAlAdha(3), HijriNewYear, ProphetsBirthday)
            .Concat(Fixed(year, (1, 1, "New Year's Day", "رأس السنة الميلادية"), (12, 1, "Commemoration Day", "يوم الشهيد"),
                (12, 2, "National Day", "عيد الاتحاد"), (12, 3, "National Day", "عيد الاتحاد"))),
    };

    public static readonly CountryPack Oman = new()
    {
        Code = "OM", NameEn = "Oman", NameAr = "سلطنة عُمان",
        Mobile = new("968", 8, ["7", "9"], "", "9XXXXXXX"),
        IdNameEn = "Civil ID number", IdNameAr = "الرقم المدني",
        Currency = "OMR", CurrencyDecimals = 3, TimeZone = "Asia/Muscat",
        HolidaysOf = year => Lunar(year, EidAlFitr(3), ArafatAndEidAlAdha(3), HijriNewYear, ProphetsBirthday, IsraAndMiraj)
            .Concat(Fixed(year, (1, 11, "Accession Day", "ذكرى تولي السلطان"), (11, 18, "National Day", "العيد الوطني"))),
    };

    public static readonly CountryPack Bahrain = new()
    {
        Code = "BH", NameEn = "Bahrain", NameAr = "مملكة البحرين",
        Mobile = new("973", 8, ["3", "6"], "", "3XXXXXXX"),
        IdNameEn = "CPR number", IdNameAr = "الرقم الشخصي",
        Currency = "BHD", CurrencyDecimals = 3, TimeZone = "Asia/Bahrain",
        HolidaysOf = year => Lunar(year, EidAlFitr(3), ArafatAndEidAlAdha(3), HijriNewYear, Ashura, ProphetsBirthday)
            .Concat(Fixed(year, (1, 1, "New Year's Day", "رأس السنة الميلادية"), (5, 1, "Labour Day", "عيد العمال"),
                (12, 16, "National Day", "العيد الوطني"), (12, 17, "National Day", "العيد الوطني"))),
    };

    public static readonly CountryPack Kuwait = new()
    {
        Code = "KW", NameEn = "Kuwait", NameAr = "دولة الكويت",
        Mobile = new("965", 8, ["4", "5", "6", "9"], "", "5XXXXXXX"),
        IdNameEn = "Civil ID number", IdNameAr = "الرقم المدني",
        Currency = "KWD", CurrencyDecimals = 3, TimeZone = "Asia/Kuwait",
        HolidaysOf = year => Lunar(year, EidAlFitr(3), ArafatAndEidAlAdha(3), HijriNewYear, ProphetsBirthday, IsraAndMiraj)
            .Concat(Fixed(year, (1, 1, "New Year's Day", "رأس السنة الميلادية"), (2, 25, "National Day", "العيد الوطني"),
                (2, 26, "Liberation Day", "عيد التحرير"))),
    };

    public static readonly CountryPack Qatar = new()
    {
        Code = "QA", NameEn = "Qatar", NameAr = "دولة قطر",
        Mobile = new("974", 8, ["3", "5", "6", "7"], "", "5XXXXXXX"),
        IdNameEn = "QID number", IdNameAr = "رقم البطاقة الشخصية",
        Currency = "QAR", TimeZone = "Asia/Qatar",
        HolidaysOf = year => Lunar(year, EidAlFitr(3), ArafatAndEidAlAdha(3))
            .Concat(Fixed(year, (12, 18, "National Day", "اليوم الوطني")))
            .Append(new Holiday(SecondTuesdayOfFebruary(year), "National Sport Day", "اليوم الرياضي للدولة", false)),
    };

    public static IReadOnlyList<CountryPack> All { get; } = [SaudiArabia, UnitedArabEmirates, Oman, Bahrain, Kuwait, Qatar];

    public const string DefaultCode = "SA";

    /// <summary>The pack for an ISO country code (any case), or null when Yardsyncs has none.</summary>
    public static CountryPack? Find(string? code) =>
        string.IsNullOrWhiteSpace(code) ? null : All.FirstOrDefault(p => p.Code.Equals(code.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The installation's pack, which reads mobile numbers and plates everywhere (set once at start-up from the
    /// <c>Country</c> setting; Saudi Arabia until then).
    /// </summary>
    public static CountryPack Installation { get; private set; } = SaudiArabia;

    public static void UseForInstallation(CountryPack pack) => Installation = pack;

    // ------------------------------------------------------------------ holidays

    private delegate IEnumerable<Holiday> LunarHoliday(int hijriYear);

    private static LunarHoliday EidAlFitr(int days) => y =>
        Enumerable.Range(1, days).Select(d => Day(y, 10, d, "Eid al-Fitr", "عيد الفطر"));

    private static LunarHoliday ArafatAndEidAlAdha(int eidDays) => y =>
        Enumerable.Range(10, eidDays).Select(d => Day(y, 12, d, "Eid al-Adha", "عيد الأضحى"))
            .Prepend(Day(y, 12, 9, "Arafat Day", "يوم عرفة"));

    private static IEnumerable<Holiday> HijriNewYear(int y) => [Day(y, 1, 1, "Islamic New Year", "رأس السنة الهجرية")];

    private static IEnumerable<Holiday> Ashura(int y) => [Day(y, 1, 9, "Ashura", "عاشوراء"), Day(y, 1, 10, "Ashura", "عاشوراء")];

    private static IEnumerable<Holiday> ProphetsBirthday(int y) => [Day(y, 3, 12, "Prophet's Birthday", "المولد النبوي")];

    private static IEnumerable<Holiday> IsraAndMiraj(int y) => [Day(y, 7, 27, "Isra and Mi'raj", "الإسراء والمعراج")];

    /// <summary>The lunar holidays of every Hijri year that touches the Gregorian year.</summary>
    private static IEnumerable<Holiday> Lunar(int year, params LunarHoliday[] holidays)
    {
        if (year < 1901 || year > 2076)
            yield break; // outside the Umm al-Qura tables
        var first = Hijri.GetYear(new DateTime(year, 1, 1));
        var last = Hijri.GetYear(new DateTime(year, 12, 31));
        for (var y = first; y <= last; y++)
            foreach (var holiday in holidays)
                foreach (var day in holiday(y))
                    yield return day;
    }

    private static Holiday Day(int hijriYear, int month, int day, string en, string ar) =>
        new(DateOnly.FromDateTime(Hijri.ToDateTime(hijriYear, month, day, 0, 0, 0, 0)), en, ar, true);

    private static IEnumerable<Holiday> Fixed(int year, params (int Month, int Day, string En, string Ar)[] days) =>
        days.Select(d => new Holiday(new DateOnly(year, d.Month, d.Day), d.En, d.Ar, false));

    private static DateOnly SecondTuesdayOfFebruary(int year)
    {
        var first = new DateOnly(year, 2, 1);
        var offset = ((int)DayOfWeek.Tuesday - (int)first.DayOfWeek + 7) % 7;
        return first.AddDays(offset + 7);
    }
}
