using Yardsyncs.Infrastructure.Brands;

namespace Yardsyncs.Infrastructure.Tests;

/// <summary>Branding checks that need no database: colour contrast and the logo's real file type.</summary>
public class BrandingRulesTests
{
    [Theory]
    [InlineData("#0e7c86", true)]  // Yardsyncs teal
    [InlineData("#ffffff", false)]
    [InlineData("#ff9900", false)] // orange: white text unreadable
    [InlineData("#000000", true)]
    [InlineData("#1a73e8", true)]
    public void A_main_colour_must_carry_white_text(string color, bool ok) =>
        Assert.Equal(ok, BrandingService.ContrastWithWhite(color) >= 4.5);

    [Fact]
    public void Contrast_matches_the_wcag_formula()
    {
        Assert.Equal(21, BrandingService.ContrastWithWhite("#000000"), 1);
        Assert.Equal(1, BrandingService.ContrastWithWhite("#ffffff"), 1);
    }

    [Fact]
    public void The_logo_type_is_read_from_the_file_itself()
    {
        Assert.Equal("image/png", BrandingService.ImageType([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0]));
        Assert.Equal("image/jpeg", BrandingService.ImageType([0xFF, 0xD8, 0xFF, 0xE0, 1]));
        Assert.Equal("image/webp", BrandingService.ImageType("RIFF\0\0\0\0WEBPVP8 "u8.ToArray()));
        Assert.Null(BrandingService.ImageType("<svg xmlns='http://www.w3.org/2000/svg'><script/></svg>"u8.ToArray()));
        Assert.Null(BrandingService.ImageType("GIF89a"u8.ToArray()));
        Assert.Null(BrandingService.ImageType([]));
    }
}

/// <summary>The charges CSV: amounts in major units, and cells that a spreadsheet would run as formulas are defused.</summary>
public class ChargeCsvTests
{
    [Fact]
    public void Amounts_and_dangerous_cells()
    {
        var at = new DateTimeOffset(2026, 10, 20, 7, 0, 0, TimeSpan.Zero);
        var report = new Yardsyncs.Infrastructure.Booking.ChargeReport(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), "KWD", 3,
            [new(1, "DEMO-1", 7, "=HYPERLINK(\"x\")", Yardsyncs.Domain.Booking.ChargeKind.NoShow, 12500, "KWD", at, null, null)], [], 12500);
        var csv = Yardsyncs.Infrastructure.Booking.ChargeService.Csv(report);
        Assert.Contains(",12.500,KWD,2026-10-20T07:00:00Z,", csv);
        Assert.Contains("\"'=HYPERLINK(\"\"x\"\")\"", csv);
        Assert.Contains(",NoShow,", csv);
    }
}
