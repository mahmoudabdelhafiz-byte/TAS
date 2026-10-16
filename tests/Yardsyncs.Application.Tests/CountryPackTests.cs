using Yardsyncs.Application.Countries;
using Yardsyncs.Application.MasterData;

namespace Yardsyncs.Application.Tests;

/// <summary>Country packs: local mobile numbers, plate letters and public holidays per country.</summary>
public class CountryPackTests
{
    [Theory]
    [InlineData("AE", "050 123 4567", "+971501234567")]
    [InlineData("AE", "971501234567", "+971501234567")]
    [InlineData("OM", "9123 4567", "+96891234567")]
    [InlineData("BH", "36001234", "+97336001234")]
    [InlineData("KW", "50012345", "+96550012345")]
    [InlineData("QA", "5512 3456", "+97455123456")]
    [InlineData("OM", "+966 50 123 4567", "+966501234567")] // a number with its country code works anywhere
    [InlineData("AE", "00966501234567", "+966501234567")]
    public void Local_mobile_numbers_follow_the_country(string country, string input, string expected) =>
        Assert.Equal(expected, PhoneNumber.Normalize(input, CountryPacks.Find(country)!));

    [Fact]
    public void A_local_number_of_another_country_is_refused()
    {
        Assert.Null(PhoneNumber.Normalize("0501234567", CountryPacks.Oman)); // Saudi local form in Oman
        Assert.Null(PhoneNumber.Normalize("12345678", CountryPacks.Qatar));  // not a Qatari mobile
    }

    [Fact]
    public void Every_pack_reads_its_own_example_number()
    {
        foreach (var pack in CountryPacks.All)
        {
            var number = PhoneNumber.Normalize(pack.Mobile.Example.Replace('X', '1'), pack);
            Assert.NotNull(number);
            Assert.StartsWith("+" + pack.Mobile.DialCode, number);
        }
    }

    [Fact]
    public void Arabic_plate_letters_are_read_where_the_country_uses_them()
    {
        Assert.Equal("1234ABJ", PlateNumber.Normalize("أ ب ح ١٢٣٤", CountryPacks.SaudiArabia));
        Assert.Equal("1234", PlateNumber.Normalize("دبي ١٢٣٤", CountryPacks.UnitedArabEmirates));
        Assert.Equal("12345K", PlateNumber.Normalize("K 12345", CountryPacks.UnitedArabEmirates));
    }

    [Fact]
    public void Packs_are_found_by_code_in_any_case()
    {
        Assert.Same(CountryPacks.UnitedArabEmirates, CountryPacks.Find(" ae "));
        Assert.Null(CountryPacks.Find("XX"));
        Assert.Null(CountryPacks.Find(null));
        Assert.Equal(CountryPacks.All.Count, CountryPacks.All.Select(p => p.Code).Distinct().Count());
        Assert.Equal(3, CountryPacks.Kuwait.CurrencyDecimals);
    }

    [Fact]
    public void Saudi_holidays_of_a_year()
    {
        var days = CountryPacks.SaudiArabia.Holidays(2026);
        Assert.Contains(days, h => h.Date == new DateOnly(2026, 2, 22) && h.NameEn == "Founding Day" && !h.Lunar);
        Assert.Contains(days, h => h.Date == new DateOnly(2026, 9, 23) && h.NameEn == "National Day");

        var fitr = days.Where(h => h.NameEn == "Eid al-Fitr").Select(h => h.Date).ToList();
        Assert.Equal(3, fitr.Count);
        Assert.All(fitr, d => Assert.Equal(3, d.Month)); // late March 2026
        Assert.Equal(fitr[0].AddDays(2), fitr[2]);

        var adha = days.Where(h => h.NameEn == "Eid al-Adha").Select(h => h.Date).ToList();
        var arafat = Assert.Single(days, h => h.NameEn == "Arafat Day");
        Assert.Equal(arafat.Date.AddDays(1), adha[0]);
        Assert.Equal(5, arafat.Date.Month); // late May 2026
        Assert.True(arafat.Lunar);
        Assert.Equal(days.OrderBy(h => h.Date).Select(h => h.Date), days.Select(h => h.Date));
    }

    [Fact]
    public void A_lunar_holiday_can_come_twice_in_one_year()
    {
        // The Hijri year is about 11 days shorter: in 2033 Eid al-Fitr starts in January and again in December.
        var starts = CountryPacks.SaudiArabia.Holidays(2033).Where(h => h.NameEn == "Eid al-Fitr").Select(h => h.Date.Month).Distinct().ToList();
        Assert.Equal(new[] { 1, 12 }, starts);
    }

    [Fact]
    public void Each_country_has_its_own_days()
    {
        Assert.Contains(CountryPacks.UnitedArabEmirates.Holidays(2026), h => h.Date == new DateOnly(2026, 12, 2) && h.NameAr == "عيد الاتحاد");
        Assert.Contains(CountryPacks.Qatar.Holidays(2026), h => h.Date == new DateOnly(2026, 2, 10) && h.NameEn == "National Sport Day");
        Assert.Contains(CountryPacks.Kuwait.Holidays(2027), h => h.Date == new DateOnly(2027, 2, 26));
        Assert.DoesNotContain(CountryPacks.UnitedArabEmirates.Holidays(2026), h => h.NameEn == "Founding Day");
        Assert.All(CountryPacks.All, p => Assert.NotEmpty(p.Holidays(2026)));
    }
}
