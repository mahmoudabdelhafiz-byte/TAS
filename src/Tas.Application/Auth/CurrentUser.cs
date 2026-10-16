using System.Security.Claims;
using Tas.Domain.Parties;

namespace Tas.Application.Auth;

/// <summary>Claim names used in TAS access tokens.</summary>
public static class TasClaims
{
    public const string UserId = "sub";
    public const string Email = "email";
    public const string Name = "name";
    public const string Role = "role";
    public const string CompanyId = "company_id";
    public const string TerminalId = "terminal_id";
    public const string OperatorId = "operator_id";

    /// <summary>
    /// The operator's terminals, comma-separated. Never in a token: added on each request from the database, so a
    /// terminal joining or leaving the operator counts at once.
    /// </summary>
    public const string OperatorTerminals = "operator_terminals";
}

/// <summary>Claim names in driver-app tokens (a separate audience: they never open staff or company endpoints).</summary>
public static class DriverClaims
{
    public const string DriverId = "sub";
    public const string DeviceId = "dev";
    public const string Mobile = "mob";
    public const string Type = "typ";
    public const string DriverType = "driver";
}

/// <summary>The signed-in driver: one phone, identified by mobile number (a driver may work for several companies).</summary>
public sealed record CurrentDriver(int DriverId, long DeviceId, string Mobile)
{
    public static CurrentDriver? FromClaims(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true || principal.FindFirst(DriverClaims.Type)?.Value != DriverClaims.DriverType)
            return null;
        if (!int.TryParse(principal.FindFirst(DriverClaims.DriverId)?.Value, out var driverId)
            || !long.TryParse(principal.FindFirst(DriverClaims.DeviceId)?.Value, out var deviceId)
            || principal.FindFirst(DriverClaims.Mobile)?.Value is not { Length: > 0 } mobile)
            return null;
        return new CurrentDriver(driverId, deviceId, mobile);
    }
}

/// <summary>The signed-in user as read from the access token.</summary>
/// <param name="OperatorTerminals">For an operator admin: the terminals of their operator.</param>
public sealed record CurrentUser(
    int UserId, UserRole Role, int? CompanyId, int? TerminalId, int? OperatorId = null, IReadOnlyList<int>? OperatorTerminals = null)
{
    public bool IsSysAdmin => Role == UserRole.SysAdmin;
    public bool IsOperatorAdmin => Role == UserRole.OperatorAdmin;
    public bool IsCompanyUser => Role is UserRole.CompanyAdmin or UserRole.CompanyUser;
    public bool IsTerminalStaff => Role is UserRole.GateClerk or UserRole.Planner or UserRole.TerminalAdmin or UserRole.OperatorAdmin or UserRole.SysAdmin;

    /// <summary>Staff of this terminal: their own terminal, or for an operator admin any terminal of the operator.</summary>
    public bool WorksAt(int terminalId) =>
        TerminalId == terminalId || (IsOperatorAdmin && OperatorTerminals is not null && OperatorTerminals.Contains(terminalId));

    /// <summary>Administers this terminal: its terminal admin, or an admin of its operator (sys admins are checked separately).</summary>
    public bool AdministersTerminal(int terminalId) =>
        (Role == UserRole.TerminalAdmin && TerminalId == terminalId) || (IsOperatorAdmin && WorksAt(terminalId));

    /// <summary>Terminal admins and operator admins.</summary>
    public bool IsTerminalAdministrator => Role is UserRole.TerminalAdmin or UserRole.OperatorAdmin;

    /// <summary>Reads the user from validated token claims; null if anything is missing or malformed.</summary>
    public static CurrentUser? FromClaims(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true)
            return null;

        if (!int.TryParse(principal.FindFirst(TasClaims.UserId)?.Value, out var userId)
            || !Enum.TryParse<UserRole>(principal.FindFirst(TasClaims.Role)?.Value, out var role))
            return null;

        IReadOnlyList<int>? operatorTerminals = null;
        if (principal.FindFirst(TasClaims.OperatorTerminals)?.Value is { } list)
            operatorTerminals = list.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => int.TryParse(x, out var id) ? id : 0).Where(id => id > 0).ToList();

        return new CurrentUser(
            userId,
            role,
            int.TryParse(principal.FindFirst(TasClaims.CompanyId)?.Value, out var companyId) ? companyId : null,
            int.TryParse(principal.FindFirst(TasClaims.TerminalId)?.Value, out var terminalId) ? terminalId : null,
            int.TryParse(principal.FindFirst(TasClaims.OperatorId)?.Value, out var operatorId) ? operatorId : null,
            operatorTerminals);
    }
}
