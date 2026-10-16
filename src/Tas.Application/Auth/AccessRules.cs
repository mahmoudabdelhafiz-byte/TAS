using Tas.Domain.Parties;

namespace Tas.Application.Auth;

/// <summary>
/// Who may do what.
///
///   Company users (CompanyAdmin, CompanyUser) — any terminal, but only for their own company.
///   Terminal staff (Planner, GateClerk, TerminalAdmin) — only their own terminal, for any company.
///     Planner / TerminalAdmin also manage capacity; GateClerk works the gate.
///   OperatorAdmin — a TerminalAdmin at every terminal of their operator.
///   SysAdmin — everything.
/// </summary>
public static class AccessRules
{
    /// <summary>Can see availability and appointments at this terminal at all.</summary>
    public static bool CanAccessTerminal(CurrentUser user, int terminalId) =>
        user.IsSysAdmin || user.IsCompanyUser || (user.IsTerminalStaff && user.WorksAt(terminalId));

    /// <summary>Can see capacity configuration, the slot board and the audit log (terminal staff, not companies).</summary>
    public static bool CanViewCapacity(CurrentUser user, int terminalId) =>
        user.IsSysAdmin || (user.IsTerminalStaff && user.WorksAt(terminalId));

    /// <summary>Can change templates, overrides and regenerate slots.</summary>
    public static bool CanManageCapacity(CurrentUser user, int terminalId) =>
        user.IsSysAdmin
        || (user.Role == UserRole.Planner && user.WorksAt(terminalId))
        || user.AdministersTerminal(terminalId);

    /// <summary>
    /// The company a booking/listing is for. Company users always act for their own company
    /// (a different requested company is refused); terminal staff must say which company.
    /// </summary>
    public static (int? CompanyId, string? Error) ResolveCompany(CurrentUser user, int? requestedCompanyId, bool required)
    {
        if (user.IsCompanyUser)
        {
            if (user.CompanyId is null)
                return (null, "Your account is not linked to a company.");
            if (requestedCompanyId is not null && requestedCompanyId != user.CompanyId)
                return (null, "You can only act for your own company.");
            return (user.CompanyId, null);
        }

        if (required && requestedCompanyId is null)
            return (null, "companyId is required when booking on behalf of a company.");
        return (requestedCompanyId, null);
    }

    /// <summary>Work the gate: arrivals, gate-in/out, trouble desk, gate board — any staff of the terminal.</summary>
    public static bool CanOperateGate(CurrentUser user, int terminalId) =>
        user.IsSysAdmin || (user.IsTerminalStaff && user.WorksAt(terminalId));

    // ---------------------------------------------------------------- master data

    /// <summary>Register companies, change their details and suspend them: terminal / operator admins and sys admins.</summary>
    public static bool CanManageCompanies(CurrentUser user) =>
        user.IsSysAdmin || user.IsTerminalAdministrator;

    /// <summary>See a company and its fleet: its own users, or any terminal staff.</summary>
    public static bool CanViewCompany(CurrentUser user, int companyId) =>
        user.IsTerminalStaff || (user.IsCompanyUser && user.CompanyId == companyId);

    /// <summary>Add / edit trucks and drivers: the company's admin, or terminal admins / sys admins.</summary>
    public static bool CanManageFleet(CurrentUser user, int companyId) =>
        user.IsSysAdmin || user.IsTerminalAdministrator
        || (user.Role == UserRole.CompanyAdmin && user.CompanyId == companyId);

    /// <summary>Block / unblock trucks and drivers: a terminal decision.</summary>
    public static bool CanBlockFleet(CurrentUser user) =>
        user.IsSysAdmin || user.IsTerminalAdministrator;

    /// <summary>Define the terminal's transaction categories.</summary>
    public static bool CanManageCategories(CurrentUser user, int terminalId) =>
        user.IsSysAdmin || user.AdministersTerminal(terminalId);

    /// <summary>Record multipurpose cargo releases (BL / DO, trips, tonnage).</summary>
    public static bool CanManageCargo(CurrentUser user, int terminalId) => CanManageCapacity(user, terminalId);

    /// <summary>
    /// Whether <paramref name="actor"/> may create a user with this role for this company / terminal / operator.
    ///   SysAdmin: anyone.
    ///   OperatorAdmin: admins of their own operator, staff of the operator's terminals, and company users of any company.
    ///   TerminalAdmin: staff of their own terminal, and company users of any company.
    ///   CompanyAdmin: company users of their own company.
    /// </summary>
    public static bool CanCreateUser(CurrentUser actor, UserRole role, int? companyId, int? terminalId, int? operatorId = null)
    {
        var isCompanyRole = role is UserRole.CompanyAdmin or UserRole.CompanyUser;
        var shapeOk = role switch
        {
            UserRole.CompanyAdmin or UserRole.CompanyUser => companyId is not null && terminalId is null && operatorId is null,
            UserRole.SysAdmin => companyId is null && terminalId is null && operatorId is null,
            UserRole.OperatorAdmin => operatorId is not null && companyId is null && terminalId is null,
            _ => terminalId is not null && companyId is null && operatorId is null,
        };
        if (!shapeOk)
            return false;

        return actor.Role switch
        {
            UserRole.SysAdmin => true,
            UserRole.OperatorAdmin => isCompanyRole
                || (role == UserRole.OperatorAdmin && operatorId == actor.OperatorId)
                || (role is not (UserRole.SysAdmin or UserRole.OperatorAdmin) && terminalId is { } t && actor.WorksAt(t)),
            UserRole.TerminalAdmin => isCompanyRole
                || (role is not (UserRole.SysAdmin or UserRole.OperatorAdmin) && terminalId == actor.TerminalId),
            UserRole.CompanyAdmin => isCompanyRole && companyId == actor.CompanyId,
            _ => false,
        };
    }

    /// <summary>Set up operators and put terminals in them: system admins only.</summary>
    public static bool CanManageOperators(CurrentUser user) => user.IsSysAdmin;
}
