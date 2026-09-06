using System.Text;
using System.Text.Json;

namespace Job.Api.Services;

/// <summary>
/// Publishes job events to search-svc via direct HTTP sync (PBL6-19 fallback path).
/// Fire-and-forget: never fails job CRUD when search is unavailable.
/// Kafka outbox publishing lands in SHOULD phase (W5) once the broker client is cached in CI.
/// </summary>
public class SearchSyncPublisher
{
    private readonly HttpClient _http;
    private readonly string _syncUrl;
    private readonly ILogger<SearchSyncPublisher> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public SearchSyncPublisher(HttpClient http, IConfiguration configuration, ILogger<SearchSyncPublisher> logger)
    {
        _http = http;
        _syncUrl = (configuration["SEARCH_SYNC_URL"] ?? configuration["SearchSync:Url"] ?? "").Trim().TrimEnd('/');
        _logger = logger;
    }

    public bool Enabled => !string.IsNullOrWhiteSpace(_syncUrl);

    public async Task PublishAsync(object payload, CancellationToken ct = default)
    {
        if (!Enabled)
        {
            return;
        }

        try
        {
            using var response = await _http.PostAsync(
                $"{_syncUrl}/internal/jobs/sync",
                new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json"),
                ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Search sync HTTP {Status} for {Url}", (int)response.StatusCode, _syncUrl);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Search sync failed (non-blocking) for {Url}", _syncUrl);
        }
    }

    public static object JobEvent(
        string eventType,
        Guid jobId, string title, string description, Guid companyId, string? companyName,
        string location, decimal? salaryMin, decimal? salaryMax, string currency,
        Guid? categoryId, string? categoryName, string? requirements, string? benefits,
        string employmentType, string experienceLevel, Guid recruiterId, string status = "Active") => new Dictionary<string, object?>
        {
            ["event_type"] = eventType,
            ["job_id"] = jobId.ToString(),
            ["title"] = title,
            ["description"] = description,
            ["company_id"] = companyId.ToString(),
            ["company_name"] = companyName ?? "",
            ["location"] = location,
            ["salary_min"] = salaryMin,
            ["salary_max"] = salaryMax,
            ["currency"] = currency,
            ["category_id"] = categoryId?.ToString(),
            ["category_name"] = categoryName,
            ["employment_type"] = employmentType,
            ["experience_level"] = experienceLevel,
            ["status"] = status,
            ["recruiter_id"] = recruiterId.ToString(),
            ["requirements"] = requirements,
            ["benefits"] = benefits,
        };

    public static object CreatedEvent(
        Guid jobId, string title, string description, Guid companyId, string? companyName,
        string location, decimal? salaryMin, decimal? salaryMax, string currency,
        Guid? categoryId, string? categoryName, string? requirements, string? benefits,
        string employmentType, string experienceLevel, Guid recruiterId) =>
        JobEvent("job.created", jobId, title, description, companyId, companyName, location,
            salaryMin, salaryMax, currency, categoryId, categoryName, requirements,
            benefits, employmentType, experienceLevel, recruiterId);

    public static object UpdatedEvent(
        Guid jobId, string title, string description, Guid companyId, string? companyName,
        string location, decimal? salaryMin, decimal? salaryMax, string currency,
        Guid? categoryId, string? categoryName, string? requirements, string? benefits,
        string employmentType, string experienceLevel, Guid recruiterId) =>
        JobEvent("job.updated", jobId, title, description, companyId, companyName, location,
            salaryMin, salaryMax, currency, categoryId, categoryName, requirements,
            benefits, employmentType, experienceLevel, recruiterId);

    public static object DeletedEvent(Guid jobId) => new Dictionary<string, object?>
    {
        ["event_type"] = "job.deleted",
        ["job_id"] = jobId.ToString(),
    };
}
