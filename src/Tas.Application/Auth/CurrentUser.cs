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
}

/// <summary>The signed-in user as read from the access token.</summary>
public sealed record CurrentUser(int UserId, UserRole Role, int? CompanyId, int? TerminalId)
{
    public bool IsSysAdmin => Role == UserRole.SysAdmin;
    public bool IsCompanyUser => Role is UserRole.CompanyAdmin or UserRole.CompanyUser;
    public bool IsTerminalStaff => Role is UserRole.GateClerk or UserRole.Planner or UserRole.TerminalAdmin or UserRole.SysAdmin;

    /// <summary>Reads the user from validated token claims; null if anything is missing or malformed.</summary>
    public static CurrentUser? FromClaims(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true)
            return null;

        if (!int.TryParse(principal.FindFirst(TasClaims.UserId)?.Value, out var userId)
            || !Enum.TryParse<UserRole>(principal.FindFirst(TasClaims.Role)?.Value, out var role))
            return null;

        return new CurrentUser(
            userId,
            role,
            int.TryParse(principal.FindFirst(TasClaims.CompanyId)?.Value, out var companyId) ? companyId : null,
            int.TryParse(principal.FindFirst(TasClaims.TerminalId)?.Value, out var terminalId) ? terminalId : null);
    }
}
