using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Yardsyncs.Application.Auth;
using Yardsyncs.Application.Gate;
using Yardsyncs.Domain.Parties;
using Yardsyncs.Infrastructure.Persistence;

namespace Yardsyncs.Infrastructure.Auth;

/// <summary>Names of the driver-app authentication scheme and policy.</summary>
public static class DriverAuth
{
    public const string Scheme = "Driver";
    public const string Policy = "Driver";
}

public static class AuthSetup
{
    /// <summary>JWT bearer authentication for the API, plus the auth services.</summary>
    public static IServiceCollection AddYardsyncsAuthentication(this IServiceCollection services, IConfiguration config)
    {
        services.AddOptions<JwtOptions>()
            .Bind(config.GetSection("Jwt"))
            .Validate(o => o.HasValidKey, "Jwt:SigningKey must be at least 32 bytes. Set it in configuration or the Jwt__SigningKey environment variable.")
            .ValidateOnStart();

        // People sign in for a token; the terminal's own gate system sends its key in X-Api-Key instead.
        services.AddAuthentication(GateSystemAuth.Selector)
            .AddPolicyScheme(GateSystemAuth.Selector, "Sign-in token or gate system key", o => o.ForwardDefaultSelector = context =>
                context.Request.Headers.ContainsKey(GateSystemKeys.Header) ? GateSystemAuth.Scheme : JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer()
            .AddScheme<AuthenticationSchemeOptions, GateSystemAuthHandler>(GateSystemAuth.Scheme, null);

        // Configured lazily so the key is read from the final configuration.
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<JwtOptions>>((bearer, jwtOptions) =>
            {
                var jwt = jwtOptions.Value;
                bearer.MapInboundClaims = false;
                bearer.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = jwt.Issuer,
                    ValidateAudience = true,
                    ValidAudience = jwt.Audience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = jwt.Key(),
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = YardsyncsClaims.Name,
                    RoleClaimType = YardsyncsClaims.Role,
                };

                // A valid signature is not enough: the account must still be active with the same role
                // and company/terminal. Deactivating a user or changing their role takes effect at once.
                bearer.Events = new JwtBearerEvents
                {
                    OnTokenValidated = async context =>
                    {
                        var claimed = CurrentUser.FromClaims(context.Principal!);
                        if (claimed is null)
                        {
                            context.Fail("Malformed token.");
                            return;
                        }

                        var db = context.HttpContext.RequestServices.GetRequiredService<YardsyncsDbContext>();
                        var ct = context.HttpContext.RequestAborted;
                        var actual = await db.AppUsers.AsNoTracking()
                            .Where(u => u.UserId == claimed.UserId)
                            .Select(u => new { u.IsActive, u.Role, u.CompanyId, u.TerminalId, u.OperatorId })
                            .SingleOrDefaultAsync(ct);

                        if (actual is null || !actual.IsActive || actual.Role != claimed.Role
                            || actual.CompanyId != claimed.CompanyId || actual.TerminalId != claimed.TerminalId
                            || actual.OperatorId != claimed.OperatorId)
                        {
                            context.Fail("The account was changed or deactivated. Please sign in again.");
                            return;
                        }

                        // An operator admin works at the operator's terminals as they are now; a stopped operator signs them out.
                        var identity = (ClaimsIdentity)context.Principal!.Identity!;
                        foreach (var stray in identity.FindAll(YardsyncsClaims.OperatorTerminals).ToList())
                            identity.RemoveClaim(stray);
                        if (actual.Role == UserRole.OperatorAdmin)
                        {
                            if (actual.OperatorId is not { } operatorId
                                || !await db.TerminalOperators.AsNoTracking().AnyAsync(o => o.OperatorId == operatorId && o.IsActive, ct))
                            {
                                context.Fail("The operator was deactivated. Please sign in again.");
                                return;
                            }
                            var terminals = await db.Terminals.AsNoTracking()
                                .Where(t => t.OperatorId == operatorId).OrderBy(t => t.TerminalId).Select(t => t.TerminalId).ToListAsync(ct);
                            identity.AddClaim(new Claim(YardsyncsClaims.OperatorTerminals, string.Join(',', terminals)));
                        }
                    },
                };
            });

        // Driver app: its own scheme and audience; the token is only valid while its device is the driver's active one.
        services.AddAuthentication().AddJwtBearer(DriverAuth.Scheme);
        services.AddOptions<JwtBearerOptions>(DriverAuth.Scheme)
            .Configure<IOptions<JwtOptions>>((bearer, jwtOptions) =>
            {
                var jwt = jwtOptions.Value;
                bearer.MapInboundClaims = false;
                bearer.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = jwt.Issuer,
                    ValidateAudience = true,
                    ValidAudience = TokenService.DriverAudience(jwt),
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = jwt.Key(),
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                };
                bearer.Events = new JwtBearerEvents
                {
                    OnTokenValidated = async context =>
                    {
                        var driver = CurrentDriver.FromClaims(context.Principal!);
                        if (driver is null)
                        {
                            context.Fail("Malformed token.");
                            return;
                        }

                        var db = context.HttpContext.RequestServices.GetRequiredService<YardsyncsDbContext>();
                        var ok = await db.DriverDevices.AsNoTracking()
                            .AnyAsync(d => d.DeviceId == driver.DeviceId && d.IsActive && d.DriverId == driver.DriverId
                                           && db.Drivers.Any(x => x.DriverId == d.DriverId && !x.IsBlocked && x.Mobile == driver.Mobile),
                                context.HttpContext.RequestAborted);
                        if (!ok)
                            context.Fail("Signed in on another phone, or the driver was blocked. Please sign in again.");
                    },
                };
            });

        services.AddAuthorization(o => o.AddPolicy(DriverAuth.Policy, p => p.AddAuthenticationSchemes(DriverAuth.Scheme).RequireAuthenticatedUser()));

        services.AddSingleton<PasswordService>();
        services.AddSingleton<TokenService>();
        services.AddScoped<AuthService>();
        services.AddScoped<UserAdminService>();
        services.Configure<PasswordLinkOptions>(config.GetSection("PasswordLinks"));
        services.AddScoped<PasswordLinkService>();
        services.AddScoped<AdminBootstrapper>();
        return services;
    }
}
