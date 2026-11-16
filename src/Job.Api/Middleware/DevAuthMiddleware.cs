using System.Security.Claims;

namespace Job.Api.Middleware;

/// <summary>
/// Opt-in switch for the development header auth. <c>ENABLE_DEV_AUTH=true</c> is required even in
/// Development (same contract as app-svc): <c>ASPNETCORE_ENVIRONMENT=Development</c> alone must not
/// trust caller-supplied <c>X-User-Id</c>/<c>X-User-Role</c> headers.
/// </summary>
public static class DevAuthPolicy
{
    public const string SettingKey = "ENABLE_DEV_AUTH";

    /// <summary>Parses the flag explicitly; anything except "true" (case-insensitive) is disabled.</summary>
    public static bool IsEnabled(IConfiguration configuration) =>
        string.Equals(configuration[SettingKey], "true", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Pipeline registration for the opt-in development header auth.</summary>
public static class DevAuthRegistration
{
    /// <summary>
    /// Adds <see cref="DevAuthMiddleware"/> only in Development with ENABLE_DEV_AUTH=true. Outside those
    /// conditions it does nothing, so Development alone never trusts caller-supplied identity headers.
    /// </summary>
    public static IApplicationBuilder UseDevAuthIfEnabled(this IApplicationBuilder app)
    {
        var environment = app.ApplicationServices.GetRequiredService<IWebHostEnvironment>();
        var configuration = app.ApplicationServices.GetRequiredService<IConfiguration>();
        var logger = app.ApplicationServices
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger(nameof(DevAuthMiddleware));

        if (!environment.IsDevelopment() || !DevAuthPolicy.IsEnabled(configuration))
        {
            logger.LogInformation(
                "DevAuthMiddleware disabled: requires Development and ENABLE_DEV_AUTH=true.");
            return app;
        }

        logger.LogWarning(
            "DevAuthMiddleware enabled: trusting X-User-Id/X-User-Role headers (ENABLE_DEV_AUTH=true, Development only).");
        app.UseMiddleware<DevAuthMiddleware>();
        return app;
    }
}

/// <summary>
/// DEVELOPMENT ONLY — Reads X-User-Id and X-User-Role headers injected by Gateway (GW-01)
/// or Postman and creates a ClaimsPrincipal so endpoints can read ctx.User without a real JWT.
/// In Production, real JWT Bearer validation is used (see Program.cs).
/// SECURITY: only registered in Development when ENABLE_DEV_AUTH=true (see Program.cs).
/// Never enable in Production — gateway is the sole trusted issuer of X-User-* headers.
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
            // Recruiter company from the gateway, which copies the JWT company_id claim (GW-01).
            var company = context.Request.Headers["X-User-Company"].FirstOrDefault();
            if (Guid.TryParse(company, out var companyId) && companyId != Guid.Empty)
                claims.Add(new Claim("company_id", companyId.ToString()));
            context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Gateway"));
        }

        await _next(context);
    }
}
