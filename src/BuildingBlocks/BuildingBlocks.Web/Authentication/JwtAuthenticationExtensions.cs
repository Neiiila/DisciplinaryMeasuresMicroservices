using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace BuildingBlocks.Web.Authentication;

/// <summary>Role names carried in the token's role claim.</summary>
public static class Roles
{
    public const string Guest = "Guest";
    public const string Employee = "Employee";
    public const string Administrator = "Administrator";
}

public static class Policies
{
    public const string Administrator = "Administrator";
}

/// <summary>
/// Shared JWT bearer configuration.
/// </summary>
/// <remarks>
/// Identity is the only service that issues tokens; every other service validates
/// them with the same parameters. Centralising the setup is what makes that
/// symmetry hold — a service configuring its own validation is how an audience or
/// issuer check quietly drifts and stops being enforced.
/// </remarks>
public static class JwtAuthenticationExtensions
{
    public static IServiceCollection AddPlatformJwtAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var section = configuration.GetSection("Jwt");
        var signingKey = section["Key"]
            ?? throw new InvalidOperationException(
                "Configuration 'Jwt:Key' is required. Supply it through the environment as Jwt__Key.");

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidIssuer = section["Issuer"],
                    ValidAudience = section["Audience"],
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
                    // Defaults to five minutes, which quietly extends every token's life.
                    ClockSkew = TimeSpan.Zero,
                    NameClaimType = ClaimTypes.NameIdentifier,
                    RoleClaimType = ClaimTypes.Role
                };

                // A WebSocket handshake cannot carry an Authorization header, so the
                // token arrives as a query parameter for hub routes only.
                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        var accessToken = context.Request.Query["access_token"];

                        if (!string.IsNullOrEmpty(accessToken)
                            && context.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                        {
                            context.Token = accessToken;
                        }

                        return Task.CompletedTask;
                    }
                };
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.Administrator, policy => policy.RequireRole(Roles.Administrator));

        return services;
    }
}
