using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace Job.Infrastructure.Clients;

/// <summary>Outcome of asking auth-svc to make a user a Recruiter of a Company.</summary>
public enum LinkCompanyResult
{
    /// <summary>auth-svc set the role and company (also when it was already that Company).</summary>
    Linked,

    /// <summary>The account already belongs to a different Company (auth-svc 409).</summary>
    AlreadyHasCompany,

    /// <summary>Not configured, unreachable, timed out or a non-2xx answer: nothing changed.</summary>
    Unavailable,
}

/// <summary>Calls auth-svc <c>POST /api/internal/users/{userId}/link-company</c> (SRS AUTH-01-12, AUTH-01-14).</summary>
public interface IAuthCompanyLinker
{
    Task<LinkCompanyResult> LinkCompanyAsync(Guid userId, Guid companyId, CancellationToken ct);
}

/// <summary>
/// HTTP client for the auth-svc link call. Reads <c>AUTH_SERVICE_URL</c> and
/// <c>AUTH_INTERNAL_TOKEN_FOR_JOB</c> at call time, so a missing value fails the one request
/// (503 to the caller) and not the whole service start.
/// </summary>
public sealed class AuthCompanyLinker : IAuthCompanyLinker
{
    public const string BaseUrlKey = "AUTH_SERVICE_URL";
    public const string TokenKey = "AUTH_INTERNAL_TOKEN_FOR_JOB";

    private readonly HttpClient _http;
    private readonly IConfiguration _config;

    public AuthCompanyLinker(HttpClient http, IConfiguration config)
    {
        _http = http;
        _config = config;
    }

    public async Task<LinkCompanyResult> LinkCompanyAsync(Guid userId, Guid companyId, CancellationToken ct)
    {
        var baseUrl = _config[BaseUrlKey]?.Trim().TrimEnd('/');
        var token = _config[TokenKey];
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(token))
            return LinkCompanyResult.Unavailable;

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{baseUrl}/api/internal/users/{userId:D}/link-company");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { companyId }),
            Encoding.UTF8,
            "application/json");

        try
        {
            using var response = await _http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
                return LinkCompanyResult.Linked;
            if (response.StatusCode == HttpStatusCode.Conflict)
                return LinkCompanyResult.AlreadyHasCompany;
            return LinkCompanyResult.Unavailable;
        }
        catch (HttpRequestException)
        {
            return LinkCompanyResult.Unavailable;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return LinkCompanyResult.Unavailable;
        }
    }
}
