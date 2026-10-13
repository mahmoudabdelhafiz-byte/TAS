using Tas.Domain.Parties;

namespace Tas.Application.Auth;

/// <summary>
/// Who may do what.
///
///   Company users (CompanyAdmin, CompanyUser) — any terminal, but only for their own company.
///   Terminal staff (Planner, GateClerk, TerminalAdmin) — only their own terminal, for any company.
///     Planner / TerminalAdmin also manage capacity; GateClerk works the gate.
///   SysAdmin — everything.
/// </summary>
public static class AccessRules
{
    /// <summary>Can see availability and appointments at this terminal at all.</summary>
    public static bool CanAccessTerminal(CurrentUser user, int terminalId) =>
        user.IsSysAdmin || user.IsCompanyUser || (user.IsTerminalStaff && user.TerminalId == terminalId);

    /// <summary>Can see capacity configuration, the slot board and the audit log (terminal staff, not companies).</summary>
    public static bool CanViewCapacity(CurrentUser user, int terminalId) =>
        user.IsSysAdmin || (user.IsTerminalStaff && user.TerminalId == terminalId);

    /// <summary>Can change templates, overrides and regenerate slots.</summary>
    public static bool CanManageCapacity(CurrentUser user, int terminalId) =>
        user.IsSysAdmin
        || ((user.Role is UserRole.Planner or UserRole.TerminalAdmin) && user.TerminalId == terminalId);

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

    /// <summary>
    /// Whether <paramref name="actor"/> may create a user with this role for this company / terminal.
    ///   SysAdmin: anyone.
    ///   TerminalAdmin: staff of their own terminal, and company users of any company.
    ///   CompanyAdmin: company users of their own company.
    /// </summary>
    public static bool CanCreateUser(CurrentUser actor, UserRole role, int? companyId, int? terminalId)
    {
        var isCompanyRole = role is UserRole.CompanyAdmin or UserRole.CompanyUser;
        var shapeOk = isCompanyRole
            ? companyId is not null && terminalId is null
            : role == UserRole.SysAdmin
                ? companyId is null && terminalId is null
                : terminalId is not null && companyId is null;
        if (!shapeOk)
            return false;

        return actor.Role switch
        {
            UserRole.SysAdmin => true,
            UserRole.TerminalAdmin => isCompanyRole || (role != UserRole.SysAdmin && terminalId == actor.TerminalId),
            UserRole.CompanyAdmin => isCompanyRole && companyId == actor.CompanyId,
            _ => false,
        };
    }
}
