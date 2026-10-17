using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Yardsyncs.Application.Auth;
using Yardsyncs.Application.Gate;
using Yardsyncs.Infrastructure.Gate;

namespace Yardsyncs.Infrastructure.Auth;

/// <summary>Names of the schemes: the terminal's own gate system (X-Api-Key), and the one choosing between it and sign-in tokens.</summary>
public static class GateSystemAuth
{
    public const string Scheme = "GateSystem";

    /// <summary>The default scheme: a request with X-Api-Key is a gate system, anything else a signed-in person.</summary>
    public const string Selector = "Yardsyncs";
}

/// <summary>
/// A key in X-Api-Key: a gate system key (ysg_) acts as its gate clerk account, a company API key (ysc_) as its company
/// user account — with the same claims a sign-in token carries, so the access rules and the history treat it like a
/// person. A read-only company key also carries <see cref="CompanyApiKeys.ReadOnlyClaim"/>. Checked on every request,
/// so a revoked key stops at once.
/// </summary>
public sealed class GateSystemAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var key = Request.Headers[GateSystemKeys.Header].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(key))
            return AuthenticateResult.NoResult();

        Yardsyncs.Domain.Parties.AppUser? user;
        var readOnly = false;
        if (CompanyApiKeys.LooksLikeKey(key))
        {
            var found = await Context.RequestServices.GetRequiredService<CompanyApiKeyService>().AuthenticateAsync(key, Context.RequestAborted);
            user = found?.User;
            readOnly = found?.ReadOnly ?? false;
        }
        else
            user = await Context.RequestServices.GetRequiredService<GateSystemService>().AuthenticateAsync(key, Context.RequestAborted);
        if (user is null)
            return AuthenticateResult.Fail("Unknown or revoked API key.");

        var claims = new List<Claim>
        {
            new(YardsyncsClaims.UserId, user.UserId.ToString(CultureInfo.InvariantCulture)),
            new(YardsyncsClaims.Email, user.Email),
            new(YardsyncsClaims.Name, user.FullName),
            new(YardsyncsClaims.Role, user.Role.ToString()),
        };
        if (user.TerminalId is { } terminalId)
            claims.Add(new Claim(YardsyncsClaims.TerminalId, terminalId.ToString(CultureInfo.InvariantCulture)));
        if (user.CompanyId is { } companyId)
            claims.Add(new Claim(YardsyncsClaims.CompanyId, companyId.ToString(CultureInfo.InvariantCulture)));
        if (readOnly)
            claims.Add(new Claim(CompanyApiKeys.ReadOnlyClaim, "true"));
        var identity = new ClaimsIdentity(claims, Scheme.Name, YardsyncsClaims.Name, YardsyncsClaims.Role);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }
}
