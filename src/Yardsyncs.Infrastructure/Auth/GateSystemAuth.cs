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
/// A gate system key in X-Api-Key: the request acts as the key's gate clerk account, with the same claims a sign-in
/// token carries, so the access rules and the history treat it like a person at the gate. Checked on every request,
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

        var service = Context.RequestServices.GetRequiredService<GateSystemService>();
        var user = await service.AuthenticateAsync(key, Context.RequestAborted);
        if (user is null)
            return AuthenticateResult.Fail("Unknown or revoked gate system key.");

        var claims = new List<Claim>
        {
            new(YardsyncsClaims.UserId, user.UserId.ToString(CultureInfo.InvariantCulture)),
            new(YardsyncsClaims.Email, user.Email),
            new(YardsyncsClaims.Name, user.FullName),
            new(YardsyncsClaims.Role, user.Role.ToString()),
        };
        if (user.TerminalId is { } terminalId)
            claims.Add(new Claim(YardsyncsClaims.TerminalId, terminalId.ToString(CultureInfo.InvariantCulture)));
        var identity = new ClaimsIdentity(claims, Scheme.Name, YardsyncsClaims.Name, YardsyncsClaims.Role);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }
}
