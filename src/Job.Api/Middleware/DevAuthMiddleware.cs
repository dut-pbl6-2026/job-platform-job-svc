using System.Security.Claims;

namespace Job.Api.Middleware;

/// <summary>
/// DEVELOPMENT ONLY — Reads X-User-Id and X-User-Role headers injected by Gateway (GW-01)
/// or Postman and creates a ClaimsPrincipal so endpoints can read ctx.User without a real JWT.
/// In Production, real JWT Bearer validation is used (see Program.cs).
/// SECURITY: This middleware is only registered when ASPNETCORE_ENVIRONMENT=Development.
/// Never enable in Production — gateway is the sole trusted issuer of X-User-* headers.
/// For local testing without gateway, you may set X-User-Id / X-User-Role directly.
/// </summary>
public class DevAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<DevAuthMiddleware> _logger;

    public DevAuthMiddleware(RequestDelegate next, ILogger<DevAuthMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Prefer canonical GW-01 header X-User-Role, fallback to legacy X-Role for backward compat
        var userId = context.Request.Headers["X-User-Id"].FirstOrDefault();
        var role = context.Request.Headers["X-User-Role"].FirstOrDefault()
                   ?? context.Request.Headers["X-Role"].FirstOrDefault();

        // Gateway trust hint: when traffic comes via gateway, it should set X-Forwarded-By-Gateway or similar.
        // In Development we allow direct headers for Postman convenience but log a warning so misuse is visible.
        var fromGateway = context.Request.Headers.ContainsKey("X-Forwarded-By-Gateway")
                          || context.Request.Headers.ContainsKey("X-Gateway-Forwarded");

        if (!string.IsNullOrEmpty(userId) && Guid.TryParse(userId, out _))
        {
            if (!fromGateway)
            {
                _logger.LogWarning(
                    "DevAuthMiddleware: X-User-Id header received without gateway trust header. "
                    + "This is allowed in Development for Postman testing, but never enable this middleware in Production.");
            }

            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, userId),
                new(ClaimTypes.Role, role ?? "User")
            };
            context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Gateway"));
        }

        await _next(context);
    }
}
