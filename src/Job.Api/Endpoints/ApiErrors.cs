using Microsoft.AspNetCore.Http;

namespace Job.Api.Endpoints;

/// <summary>Error body with a stable <c>errorCode</c> for the company sign-up and join-request endpoints.</summary>
public static class ApiErrors
{
    public static IResult Error(int status, string errorCode, string message) =>
        Results.Json(new { errorCode, message }, statusCode: status);
}
