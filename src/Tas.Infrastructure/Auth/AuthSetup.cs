using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Tas.Application.Auth;
using Tas.Infrastructure.Persistence;

namespace Tas.Infrastructure.Auth;

public static class AuthSetup
{
    /// <summary>JWT bearer authentication for the API, plus the auth services.</summary>
    public static IServiceCollection AddTasAuthentication(this IServiceCollection services, IConfiguration config)
    {
        services.AddOptions<JwtOptions>()
            .Bind(config.GetSection("Jwt"))
            .Validate(o => o.HasValidKey, "Jwt:SigningKey must be at least 32 bytes. Set it in configuration or the Jwt__SigningKey environment variable.")
            .ValidateOnStart();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();

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
                    NameClaimType = TasClaims.Name,
                    RoleClaimType = TasClaims.Role,
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

                        var db = context.HttpContext.RequestServices.GetRequiredService<TasDbContext>();
                        var actual = await db.AppUsers.AsNoTracking()
                            .Where(u => u.UserId == claimed.UserId)
                            .Select(u => new { u.IsActive, u.Role, u.CompanyId, u.TerminalId })
                            .SingleOrDefaultAsync(context.HttpContext.RequestAborted);

                        if (actual is null || !actual.IsActive || actual.Role != claimed.Role
                            || actual.CompanyId != claimed.CompanyId || actual.TerminalId != claimed.TerminalId)
                            context.Fail("The account was changed or deactivated. Please sign in again.");
                    },
                };
            });

        services.AddAuthorization();

        services.AddSingleton<PasswordService>();
        services.AddSingleton<TokenService>();
        services.AddScoped<AuthService>();
        services.AddScoped<UserAdminService>();
        services.AddScoped<AdminBootstrapper>();
        return services;
    }
}
