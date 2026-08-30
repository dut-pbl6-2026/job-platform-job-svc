using System.Security.Claims;

namespace Job.Api.Middleware;

/// <summary>
/// DEVELOPMENT ONLY — Reads X-User-Id and X-Role headers injected by Postman or Gateway
/// and creates a ClaimsPrincipal so endpoints can read ctx.User without a real JWT.
/// In Production, real JWT Bearer validation is used (see Program.cs).
/// </summary>
public class DevAuthMiddleware
{
    private readonly RequestDelegate _next;

    public DevAuthMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        var userId = context.Request.Headers["X-User-Id"].FirstOrDefault();
        var role = context.Request.Headers["X-Role"].FirstOrDefault();

        if (!string.IsNullOrEmpty(userId) && Guid.TryParse(userId, out _))
        {
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
