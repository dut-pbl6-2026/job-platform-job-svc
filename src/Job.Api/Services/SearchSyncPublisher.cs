using System.Text;
using System.Text.Json;

namespace Job.Api.Services;

/// <summary>
/// Typed search-index document (PBL6-19 HTTP sync path).
/// Serialized with <see cref="JsonSerializerDefaults.Web"/> (camelCase properties);
/// search-svc binds case-insensitively, so both casings are accepted.
/// A record (not a string-keyed dictionary) keeps the contract compiler-checked:
/// renaming a property breaks the build instead of silently dropping a field.
/// </summary>
public sealed record JobSyncPayload(
    string Id,
    string Title,
    string Description,
    string CompanyId,
    string CompanyName,
    string Location,
    decimal? SalaryMin,
    decimal? SalaryMax,
    string Currency,
    string? CategoryId,
    string? CategoryName,
    string EmploymentType,
    string ExperienceLevel,
    string Status,
    string RecruiterId,
    string? Requirements,
    string? Benefits);

/// <summary>
/// Publishes job documents to search-svc index endpoints (PBL6-19 HTTP sync path).
/// Contract: POST {SEARCH_SYNC_URL}/api/search/index (JobSyncPayload),
/// DELETE {SEARCH_SYNC_URL}/api/search/index/{id}.
/// Best-effort: never fails job CRUD when search is unavailable, and never
/// propagates exceptions (including cancellation) to callers.
/// Known limitation (documented for the Kafka/outbox phase): no retry or
/// outbox — if search-svc is down during a write, the index diverges until
/// the next write of that job. Failures are logged with job id for triage.
/// When SEARCH_INDEX_TOKEN is set, it is sent as X-Internal-Token (must match
/// search-svc's expected token); when unset, requests carry no token.
/// Kafka outbox publishing lands in SHOULD phase (W5).
/// </summary>
public class SearchSyncPublisher
{
    private const string IndexTokenHeader = "X-Internal-Token";

    private readonly HttpClient _http;
    private readonly string _syncUrl;
    private readonly string _indexToken;
    private readonly ILogger<SearchSyncPublisher> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public SearchSyncPublisher(HttpClient http, IConfiguration configuration, ILogger<SearchSyncPublisher> logger)
    {
        _http = http;
        _syncUrl = (configuration["SEARCH_SYNC_URL"] ?? configuration["SearchSync:Url"] ?? "").Trim().TrimEnd('/');
        _indexToken = (configuration["SEARCH_INDEX_TOKEN"] ?? configuration["SearchSync:Token"] ?? "").Trim();
        _logger = logger;
    }

    public bool Enabled => !string.IsNullOrWhiteSpace(_syncUrl);

    public async Task PublishUpsertAsync(JobSyncPayload document, CancellationToken ct = default)
    {
        if (!Enabled)
        {
            return;
        }

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"{_syncUrl}/api/search/index")
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(document, JsonOptions), Encoding.UTF8, "application/json"),
            };
            AddIndexToken(request);
            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Search index sync HTTP {Status} for job {JobId} at {Url}",
                    (int)response.StatusCode, document.Id, _syncUrl);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Search index sync failed (non-blocking) for job {JobId} at {Url}", document.Id, _syncUrl);
        }
    }

    public async Task PublishDeleteAsync(Guid jobId, CancellationToken ct = default)
    {
        if (!Enabled)
        {
            return;
        }

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Delete,
                $"{_syncUrl}/api/search/index/{jobId}");
            AddIndexToken(request);
            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Search index delete HTTP {Status} for job {JobId} at {Url}",
                    (int)response.StatusCode, jobId, _syncUrl);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Search index delete failed (non-blocking) for job {JobId} at {Url}", jobId, _syncUrl);
        }
    }

    private void AddIndexToken(HttpRequestMessage request)
    {
        if (!string.IsNullOrWhiteSpace(_indexToken))
        {
            request.Headers.TryAddWithoutValidation(IndexTokenHeader, _indexToken);
        }
    }

    /// <summary>
    /// Builds the sync payload from tracked entities. Callers must pass the
    /// actual <paramref name="status"/> (e.g. <c>job.Status.ToString()</c>) —
    /// there is no safe default: indexing a Closed job as Active would make it
    /// searchable again behind the search-svc Active filter.
    /// </summary>
    public static JobSyncPayload BuildDocument(
        Guid jobId, string title, string description, Guid companyId, string? companyName,
        string location, decimal? salaryMin, decimal? salaryMax, string currency,
        Guid? categoryId, string? categoryName, string? requirements, string? benefits,
        string employmentType, string experienceLevel, Guid recruiterId, string status) => new(
            Id: jobId.ToString(),
            Title: title,
            Description: description,
            CompanyId: companyId.ToString(),
            CompanyName: companyName ?? "",
            Location: location,
            SalaryMin: salaryMin,
            SalaryMax: salaryMax,
            Currency: currency,
            CategoryId: categoryId?.ToString(),
            CategoryName: categoryName,
            EmploymentType: employmentType,
            ExperienceLevel: experienceLevel,
            Status: status,
            RecruiterId: recruiterId.ToString(),
            Requirements: requirements,
            Benefits: benefits);
}
