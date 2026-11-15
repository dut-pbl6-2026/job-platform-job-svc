using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using SharedKernel;
using System.Security.Claims;

namespace Job.Api.Auth;

/// <summary>
/// RS256 validation of access tokens issued by auth-svc. The public keys come from auth-svc's discovery
/// document (Jwt:MetadataAddress, required); this service holds no signing secret.
/// </summary>
public static class JwtBearerRs256
{
    /// <summary>Registers the JWT bearer scheme. Throws at startup when the metadata address is missing.</summary>
    public static AuthenticationBuilder AddRs256JwtBearer(this IServiceCollection services, IConfiguration configuration)
    {
        var jwt = configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
        var metadataAddress = configuration["Jwt:MetadataAddress"];
        if (string.IsNullOrWhiteSpace(metadataAddress))
            throw new InvalidOperationException(
                "Jwt:MetadataAddress is required (env Jwt__MetadataAddress: auth-svc discovery URL, e.g. http://auth-svc:5001/.well-known/openid-configuration).");

        return services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(o =>
            {
                o.MetadataAddress = metadataAddress;
                o.RequireHttpsMetadata = false; // private network, plain HTTP (see README)
                o.AutomaticRefreshInterval = TimeSpan.FromMinutes(15); // bounded JWKS refresh
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = jwt.Issuer,
                    ValidateAudience = true,
                    ValidAudience = jwt.Audience,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                    TryAllIssuerSigningKeys = false,
                    ClockSkew = TimeSpan.Zero // SEC-01
                };
                o.Events = new JwtBearerEvents
                {
                    OnTokenValidated = context =>
                    {
                        if (!HasKidAndSubject(context.SecurityToken, context.Principal))
                            context.Fail("Access token must carry a kid and a subject");
                        return Task.CompletedTask;
                    }
                };
            });
    }

    private static bool HasKidAndSubject(Microsoft.IdentityModel.Tokens.SecurityToken? token, ClaimsPrincipal? principal)
    {
        if (token is not JsonWebToken jwt || string.IsNullOrEmpty(jwt.Kid))
            return false;
        var subject = principal?.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
                      ?? principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return !string.IsNullOrWhiteSpace(subject);
    }
}
