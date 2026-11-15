using Job.Api.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace Job.Api.Auth;

/// <summary>
/// Bearer check for the auth-svc internal endpoints (<c>JOB_INTERNAL_TOKEN_FOR_AUTH</c>).
/// Fails closed: an unset token rejects every call. Never routed by the gateway.
/// </summary>
public static class InternalCallerAuth
{
    public static bool IsAuthCallerAuthorized(HttpContext ctx, IConfiguration config)
    {
        var expected = config[InternalStatsEndpoints.AuthCallerTokenKey];
        return !string.IsNullOrWhiteSpace(expected) &&
               InternalStatsEndpoints.IsAuthorized(ctx.Request.Headers.Authorization.ToString(), expected);
    }

    public static IResult Unauthorized() =>
        Results.Json(new { message = "Unauthorized" }, statusCode: 401);
}
