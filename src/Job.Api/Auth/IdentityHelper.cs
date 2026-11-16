using System.Security.Claims;

namespace Job.Api.Auth;

/// <summary>
/// Shared identity extraction for Minimal API endpoints (DRY).
/// Reads the gateway-injected identity (GW-01): NameIdentifier claim → userId,
/// Role claim → role. Returns (null, role) when the user id is missing or not
/// a valid Guid — callers map that to 401 Unauthorized.
/// </summary>
public static class IdentityHelper
{
    /// <summary>JWT claim auth-svc issues for a Recruiter's company (AUTH-01-09).</summary>
    public const string CompanyIdClaimType = "company_id";

    public static (Guid? userId, string? role) GetIdentity(HttpContext ctx)
    {
        var rawUserId = ctx.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var role = ctx.User.FindFirst(ClaimTypes.Role)?.Value;
        if (rawUserId is null || !Guid.TryParse(rawUserId, out var userId))
            return (null, role);
        return (userId, role);
    }

    /// <summary>Recruiter's company from the JWT/gateway claim, or null for older tokens and dev seeds.</summary>
    public static Guid? GetCompanyId(HttpContext ctx)
    {
        var raw = ctx.User.FindFirst(CompanyIdClaimType)?.Value;
        return Guid.TryParse(raw, out var companyId) && companyId != Guid.Empty ? companyId : null;
    }
}
