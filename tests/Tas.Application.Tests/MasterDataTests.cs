using Tas.Application.Auth;
using Tas.Application.MasterData;
using Tas.Domain;
using Tas.Domain.Booking;
using Tas.Domain.Parties;

namespace Tas.Application.Tests;

public class PlateNumberTests
{
    [Theory]
    [InlineData("1234 ABJ", "1234ABJ")]
    [InlineData("abj-1234", "1234ABJ")]          // letters first, lower case
    [InlineData("أ ب ح ١٢٣٤", "1234ABJ")]         // Arabic letters and Arabic-Indic digits
    [InlineData("ا ب ح 1234", "1234ABJ")]
    [InlineData("ر س ص ۵۶۷۸", "5678RSX")]         // extended Arabic-Indic digits
    [InlineData("ك ل م ن 12", "12KLZN")]
    public void Plates_normalize_to_one_form(string input, string expected) =>
        Assert.Equal(expected, PlateNumber.Normalize(input));

    [Theory]
    [InlineData("ABJ")]   // no digits
    [InlineData("1")]     // too short
    [InlineData("")]
    public void Unusable_plates_are_invalid(string input) => Assert.False(PlateNumber.IsValid(PlateNumber.Normalize(input)));
}

public class PhoneNumberTests
{
    [Theory]
    [InlineData("0501234567", "+966501234567")]
    [InlineData("050 123 4567", "+966501234567")]
    [InlineData("501234567", "+966501234567")]
    [InlineData("966501234567", "+966501234567")]
    [InlineData("00966501234567", "+966501234567")]
    [InlineData("+966 50-123-4567", "+966501234567")]
    [InlineData("٠٥٠١٢٣٤٥٦٧", "+966501234567")]
    [InlineData("+92 300 1234567", "+923001234567")]
    public void Mobile_numbers_become_e164(string input, string expected) => Assert.Equal(expected, PhoneNumber.Normalize(input));

    [Theory]
    [InlineData("12345")]
    [InlineData("call me")]
    [InlineData("")]
    public void Unusable_numbers_give_null(string input) => Assert.Null(PhoneNumber.Normalize(input));
}

public class MasterDataValidatorTests
{
    [Fact]
    public void Company_rules()
    {
        Assert.Empty(MasterDataValidator.Company(new CompanyInput("Al Noor Transport", "4030123456", CompanyType.Trucking)));
        Assert.Empty(MasterDataValidator.Company(new CompanyInput("No CR yet", null, CompanyType.Forwarder)));
        Assert.NotEmpty(MasterDataValidator.Company(new CompanyInput(" ", null, CompanyType.Trucking)));
        Assert.NotEmpty(MasterDataValidator.Company(new CompanyInput("X", "CR-12", CompanyType.Trucking)));
    }

    [Fact]
    public void Driver_rules_normalize_the_mobile()
    {
        var (mobile, errors) = MasterDataValidator.Driver(new DriverInput("Mohammed Khan", "2012345678", "0501234567", "ur"));
        Assert.Empty(errors);
        Assert.Equal("+966501234567", mobile);

        var (_, bad) = MasterDataValidator.Driver(new DriverInput("", "12", "abc", "fr"));
        Assert.Equal(4, bad.Count);
    }

    [Fact]
    public void Category_codes_are_upper_case_identifiers()
    {
        Assert.Empty(MasterDataValidator.Category(new CategoryInput("roro_pickup", "RoRo pickup", "استلام مركبات", CategoryMode.Cargo)));
        Assert.NotEmpty(MasterDataValidator.Category(new CategoryInput("1BAD", "x", "y", CategoryMode.Container)));
        Assert.NotEmpty(MasterDataValidator.Category(new CategoryInput("GOOD", "", "y", CategoryMode.Container)));
    }

    [Fact]
    public void Cargo_needs_a_reference_and_a_limit()
    {
        Assert.Empty(MasterDataValidator.Cargo(new CargoInput("BL-STEEL-001", null, null, "Steel", CargoType.Breakbulk, 1200, 40)));
        Assert.Empty(MasterDataValidator.Cargo(new CargoInput("DO/2026/77", null, null, null, CargoType.Bulk, 500, null)));
        Assert.NotEmpty(MasterDataValidator.Cargo(new CargoInput("BL-1", null, null, null, CargoType.Bulk, null, null)));
        Assert.NotEmpty(MasterDataValidator.Cargo(new CargoInput("BL-1", null, null, null, CargoType.Bulk, -5, null)));
        Assert.NotEmpty(MasterDataValidator.Cargo(new CargoInput("", null, null, null, CargoType.Bulk, 10, 1)));
    }
}

public class MasterDataAccessTests
{
    private static readonly CurrentUser CompanyAdmin = new(1, UserRole.CompanyAdmin, 10, null);
    private static readonly CurrentUser CompanyUser = new(2, UserRole.CompanyUser, 10, null);
    private static readonly CurrentUser Planner = new(3, UserRole.Planner, null, 5);
    private static readonly CurrentUser GateClerk = new(4, UserRole.GateClerk, null, 5);
    private static readonly CurrentUser TerminalAdmin = new(5, UserRole.TerminalAdmin, null, 5);

    [Fact]
    public void Companies_are_managed_by_terminal_admins()
    {
        Assert.True(AccessRules.CanManageCompanies(TerminalAdmin));
        Assert.False(AccessRules.CanManageCompanies(Planner));
        Assert.False(AccessRules.CanManageCompanies(CompanyAdmin));
    }

    [Fact]
    public void Fleet_is_managed_by_the_company_admin_and_blocked_by_the_terminal()
    {
        Assert.True(AccessRules.CanManageFleet(CompanyAdmin, 10));
        Assert.False(AccessRules.CanManageFleet(CompanyAdmin, 11));
        Assert.False(AccessRules.CanManageFleet(CompanyUser, 10));
        Assert.True(AccessRules.CanManageFleet(TerminalAdmin, 10));

        Assert.True(AccessRules.CanViewCompany(CompanyUser, 10));
        Assert.False(AccessRules.CanViewCompany(CompanyUser, 11));
        Assert.True(AccessRules.CanViewCompany(GateClerk, 11));

        Assert.True(AccessRules.CanBlockFleet(TerminalAdmin));
        Assert.False(AccessRules.CanBlockFleet(CompanyAdmin));
    }

    [Fact]
    public void Categories_by_terminal_admin_cargo_by_planner()
    {
        Assert.True(AccessRules.CanManageCategories(TerminalAdmin, 5));
        Assert.False(AccessRules.CanManageCategories(TerminalAdmin, 6));
        Assert.False(AccessRules.CanManageCategories(Planner, 5));
        Assert.True(AccessRules.CanManageCargo(Planner, 5));
        Assert.False(AccessRules.CanManageCargo(GateClerk, 5));
    }
}
