using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Tas.Application.Auth;

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
