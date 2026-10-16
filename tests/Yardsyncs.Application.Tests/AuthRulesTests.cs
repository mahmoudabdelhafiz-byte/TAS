using System.Security.Claims;
using Yardsyncs.Application.Auth;
using Yardsyncs.Domain.Parties;

namespace Yardsyncs.Application.Tests;

public class AccessRulesTests
{
    private static readonly CurrentUser Dispatcher = new(1, UserRole.CompanyAdmin, CompanyId: 10, TerminalId: null);
    private static readonly CurrentUser CompanyUser = new(2, UserRole.CompanyUser, CompanyId: 10, TerminalId: null);
    private static readonly CurrentUser Planner = new(3, UserRole.Planner, CompanyId: null, TerminalId: 5);
    private static readonly CurrentUser GateClerk = new(4, UserRole.GateClerk, CompanyId: null, TerminalId: 5);
    private static readonly CurrentUser TerminalAdmin = new(5, UserRole.TerminalAdmin, CompanyId: null, TerminalId: 5);
    private static readonly CurrentUser SysAdmin = new(6, UserRole.SysAdmin, CompanyId: null, TerminalId: null);

    [Fact]
    public void Company_users_reach_every_terminal_staff_only_their_own()
    {
        Assert.True(AccessRules.CanAccessTerminal(Dispatcher, 5));
        Assert.True(AccessRules.CanAccessTerminal(Dispatcher, 99));
        Assert.True(AccessRules.CanAccessTerminal(GateClerk, 5));
        Assert.False(AccessRules.CanAccessTerminal(GateClerk, 99));
        Assert.True(AccessRules.CanAccessTerminal(SysAdmin, 99));
    }

    [Fact]
    public void Only_planners_and_terminal_admins_of_the_terminal_manage_capacity()
    {
        Assert.True(AccessRules.CanManageCapacity(Planner, 5));
        Assert.True(AccessRules.CanManageCapacity(TerminalAdmin, 5));
        Assert.True(AccessRules.CanManageCapacity(SysAdmin, 5));
        Assert.False(AccessRules.CanManageCapacity(Planner, 99));
        Assert.False(AccessRules.CanManageCapacity(GateClerk, 5));
        Assert.False(AccessRules.CanManageCapacity(Dispatcher, 5));
    }

    [Fact]
    public void Only_staff_of_the_terminal_see_capacity_configuration()
    {
        Assert.True(AccessRules.CanViewCapacity(GateClerk, 5));
        Assert.True(AccessRules.CanViewCapacity(Planner, 5));
        Assert.True(AccessRules.CanViewCapacity(SysAdmin, 5));
        Assert.False(AccessRules.CanViewCapacity(Planner, 99));
        Assert.False(AccessRules.CanViewCapacity(Dispatcher, 5));
    }

    [Fact]
    public void Company_users_always_act_for_their_own_company()
    {
        AssertResolved(AccessRules.ResolveCompany(Dispatcher, null, required: true), 10);
        AssertResolved(AccessRules.ResolveCompany(Dispatcher, 10, required: true), 10);

        var (company, error) = AccessRules.ResolveCompany(Dispatcher, 11, required: true);
        Assert.Null(company);
        Assert.NotNull(error);
    }

    [Fact]
    public void Terminal_staff_must_name_the_company_when_booking()
    {
        AssertResolved(AccessRules.ResolveCompany(Planner, 10, required: true), 10);
        Assert.NotNull(AccessRules.ResolveCompany(Planner, null, required: true).Error);
        AssertResolved(AccessRules.ResolveCompany(Planner, null, required: false), null); // listing: all companies
    }

    private static void AssertResolved((int? CompanyId, string? Error) result, int? expectedCompany)
    {
        Assert.Null(result.Error);
        Assert.Equal(expectedCompany, result.CompanyId);
    }

    [Theory]
    // Company admin: users of its own company only.
    [InlineData(UserRole.CompanyAdmin, UserRole.CompanyUser, 10, null, true)]
    [InlineData(UserRole.CompanyAdmin, UserRole.CompanyAdmin, 10, null, true)]
    [InlineData(UserRole.CompanyAdmin, UserRole.CompanyUser, 11, null, false)]
    [InlineData(UserRole.CompanyAdmin, UserRole.Planner, null, 5, false)]
    // Company user: nobody.
    [InlineData(UserRole.CompanyUser, UserRole.CompanyUser, 10, null, false)]
    // Terminal admin: own terminal's staff and any company's users, never SysAdmin.
    [InlineData(UserRole.TerminalAdmin, UserRole.GateClerk, null, 5, true)]
    [InlineData(UserRole.TerminalAdmin, UserRole.TerminalAdmin, null, 5, true)]
    [InlineData(UserRole.TerminalAdmin, UserRole.Planner, null, 6, false)]
    [InlineData(UserRole.TerminalAdmin, UserRole.CompanyAdmin, 77, null, true)]
    [InlineData(UserRole.TerminalAdmin, UserRole.SysAdmin, null, null, false)]
    // Planner: nobody.
    [InlineData(UserRole.Planner, UserRole.GateClerk, null, 5, false)]
    // SysAdmin: anyone.
    [InlineData(UserRole.SysAdmin, UserRole.SysAdmin, null, null, true)]
    [InlineData(UserRole.SysAdmin, UserRole.Planner, null, 6, true)]
    public void User_creation_rules(UserRole actorRole, UserRole newRole, int? companyId, int? terminalId, bool allowed)
    {
        var actor = actorRole switch
        {
            UserRole.CompanyAdmin => Dispatcher,
            UserRole.CompanyUser => CompanyUser,
            UserRole.TerminalAdmin => TerminalAdmin,
            UserRole.Planner => Planner,
            _ => SysAdmin,
        };
        Assert.Equal(allowed, AccessRules.CanCreateUser(actor, newRole, companyId, terminalId));
    }

    [Theory]
    [InlineData(UserRole.CompanyUser, null, null)]  // company user without company
    [InlineData(UserRole.CompanyUser, 10, 5)]       // company user with a terminal
    [InlineData(UserRole.Planner, null, null)]      // staff without terminal
    [InlineData(UserRole.Planner, 10, 5)]           // staff with a company
    [InlineData(UserRole.SysAdmin, 10, null)]       // sys admin tied to a company
    public void Badly_shaped_users_are_refused_even_for_sys_admin(UserRole role, int? companyId, int? terminalId) =>
        Assert.False(AccessRules.CanCreateUser(SysAdmin, role, companyId, terminalId));
}

/// <summary>An operator admin runs every terminal of their operator, and nothing outside it.</summary>
public class OperatorAccessTests
{
    // Operator 3 has terminals 5 and 7; terminal 9 belongs to another operator.
    private static readonly CurrentUser OperatorAdmin = new(7, UserRole.OperatorAdmin, null, null, OperatorId: 3, OperatorTerminals: [5, 7]);
    private static readonly CurrentUser TerminalAdmin = new(5, UserRole.TerminalAdmin, null, TerminalId: 5, OperatorId: null);
    private static readonly CurrentUser SysAdmin = new(6, UserRole.SysAdmin, null, null);

    [Fact]
    public void An_operator_admin_works_at_every_terminal_of_the_operator_only()
    {
        foreach (var terminal in new[] { 5, 7 })
        {
            Assert.True(AccessRules.CanAccessTerminal(OperatorAdmin, terminal));
            Assert.True(AccessRules.CanViewCapacity(OperatorAdmin, terminal));
            Assert.True(AccessRules.CanManageCapacity(OperatorAdmin, terminal));
            Assert.True(AccessRules.CanOperateGate(OperatorAdmin, terminal));
            Assert.True(AccessRules.CanManageCategories(OperatorAdmin, terminal));
        }
        Assert.False(AccessRules.CanAccessTerminal(OperatorAdmin, 9));
        Assert.False(AccessRules.CanManageCapacity(OperatorAdmin, 9));
        Assert.False(AccessRules.CanOperateGate(OperatorAdmin, 9));
        Assert.False(AccessRules.CanManageCategories(OperatorAdmin, 9));

        Assert.True(AccessRules.CanManageCompanies(OperatorAdmin));
        Assert.True(AccessRules.CanBlockFleet(OperatorAdmin));
        Assert.False(AccessRules.CanManageOperators(OperatorAdmin));
        Assert.True(AccessRules.CanManageOperators(SysAdmin));
    }

    [Fact]
    public void Without_its_terminal_list_an_operator_admin_reaches_nothing()
    {
        var empty = OperatorAdmin with { OperatorTerminals = null };
        Assert.False(AccessRules.CanAccessTerminal(empty, 5));
        // A terminal admin's operator claim never widens their reach.
        Assert.False(AccessRules.CanManageCapacity(TerminalAdmin with { OperatorTerminals = [7] }, 7));
    }

    [Theory]
    // Operator admin: fellow admins of the operator, staff of its terminals, any company's users.
    [InlineData(UserRole.OperatorAdmin, null, null, 3, true)]
    [InlineData(UserRole.OperatorAdmin, null, null, 4, false)]
    [InlineData(UserRole.TerminalAdmin, null, 7, null, true)]
    [InlineData(UserRole.GateClerk, null, 5, null, true)]
    [InlineData(UserRole.Planner, null, 9, null, false)]
    [InlineData(UserRole.CompanyUser, 10, null, null, true)]
    [InlineData(UserRole.SysAdmin, null, null, null, false)]
    // Badly shaped: an operator admin tied to a terminal, staff tied to an operator.
    [InlineData(UserRole.OperatorAdmin, null, 5, 3, false)]
    [InlineData(UserRole.GateClerk, null, 5, 3, false)]
    public void Operator_admins_create(UserRole role, int? companyId, int? terminalId, int? operatorId, bool allowed) =>
        Assert.Equal(allowed, AccessRules.CanCreateUser(OperatorAdmin, role, companyId, terminalId, operatorId));

    [Fact]
    public void Terminal_admins_never_create_operator_admins()
    {
        Assert.False(AccessRules.CanCreateUser(TerminalAdmin, UserRole.OperatorAdmin, null, null, 3));
        Assert.True(AccessRules.CanCreateUser(SysAdmin, UserRole.OperatorAdmin, null, null, 3));
        Assert.False(AccessRules.CanCreateUser(SysAdmin, UserRole.OperatorAdmin, null, null, null));
    }

    [Fact]
    public void The_operator_terminal_list_is_read_from_the_claims()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(YardsyncsClaims.UserId, "7"), new Claim(YardsyncsClaims.Role, "OperatorAdmin"),
            new Claim(YardsyncsClaims.OperatorId, "3"), new Claim(YardsyncsClaims.OperatorTerminals, "5,7"),
        ], "test"));
        var user = CurrentUser.FromClaims(principal)!;
        Assert.Equal(3, user.OperatorId);
        Assert.Equal(new[] { 5, 7 }, user.OperatorTerminals!);
        Assert.True(user.WorksAt(7));
        Assert.False(user.WorksAt(9));
    }
}

public class PasswordPolicyTests
{
    [Theory]
    [InlineData("short1")]
    [InlineData("onlyletterslong")]
    [InlineData("1234567890123")]
    public void Weak_passwords_are_refused(string password) => Assert.NotNull(PasswordPolicy.Check(password));

    [Fact]
    public void Password_with_the_email_name_is_refused() =>
        Assert.NotNull(PasswordPolicy.Check("khalid-2026-secure", "khalid@alnoor.example"));

    [Fact]
    public void Good_password_passes() => Assert.Null(PasswordPolicy.Check("Harbour-Crane-42", "khalid@alnoor.example"));
}

public class CurrentUserTests
{
    private static ClaimsPrincipal Principal(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), authenticationType: "test"));

    [Fact]
    public void Reads_user_from_token_claims()
    {
        var user = CurrentUser.FromClaims(Principal(("sub", "7"), ("role", "Planner"), ("terminal_id", "3")));

        Assert.Equal(new CurrentUser(7, UserRole.Planner, null, 3), user);
        Assert.True(user!.IsTerminalStaff);
        Assert.False(user.IsCompanyUser);
    }

    [Fact]
    public void Missing_or_bad_claims_give_no_user()
    {
        Assert.Null(CurrentUser.FromClaims(Principal(("role", "Planner"))));
        Assert.Null(CurrentUser.FromClaims(Principal(("sub", "7"), ("role", "Captain"))));
        Assert.Null(CurrentUser.FromClaims(new ClaimsPrincipal(new ClaimsIdentity()))); // not authenticated
    }
}
