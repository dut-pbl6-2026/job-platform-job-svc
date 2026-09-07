using System.Text;
using System.Text.Json;

namespace Job.Api.Services;

/// <summary>
/// Publishes job documents to search-svc index endpoints (PBL6-19 HTTP sync path).
/// Contract: POST {SEARCH_SYNC_URL}/api/search/index (JobSyncDto, PascalCase),
/// DELETE {SEARCH_SYNC_URL}/api/search/index/{id}.
/// Best-effort: never fails job CRUD when search is unavailable.
/// Kafka outbox publishing lands in SHOULD phase (W5).
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

    public async Task PublishUpsertAsync(object document, CancellationToken ct = default)
    {
        if (!Enabled)
        {
            return;
        }

        try
        {
            using var response = await _http.PostAsync(
                $"{_syncUrl}/api/search/index",
                new StringContent(JsonSerializer.Serialize(document, JsonOptions), Encoding.UTF8, "application/json"),
                ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Search index sync HTTP {Status} for {Url}", (int)response.StatusCode, _syncUrl);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Search index sync failed (non-blocking) for {Url}", _syncUrl);
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
            using var response = await _http.DeleteAsync(
                $"{_syncUrl}/api/search/index/{jobId}",
                ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Search index delete HTTP {Status} for {Url}", (int)response.StatusCode, _syncUrl);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Search index delete failed (non-blocking) for {Url}", _syncUrl);
        }
    }

    public static object UpsertDocument(
        Guid jobId, string title, string description, Guid companyId, string? companyName,
        string location, decimal? salaryMin, decimal? salaryMax, string currency,
        Guid? categoryId, string? categoryName, string? requirements, string? benefits,
        string employmentType, string experienceLevel, Guid recruiterId, string status = "Active") => new Dictionary<string, object?>
        {
            ["Id"] = jobId.ToString(),
            ["Title"] = title,
            ["Description"] = description,
            ["CompanyId"] = companyId.ToString(),
            ["CompanyName"] = companyName ?? "",
            ["Location"] = location,
            ["SalaryMin"] = salaryMin,
            ["SalaryMax"] = salaryMax,
            ["Currency"] = currency,
            ["CategoryId"] = categoryId?.ToString(),
            ["CategoryName"] = categoryName,
            ["EmploymentType"] = employmentType,
            ["ExperienceLevel"] = experienceLevel,
            ["Status"] = status,
            ["RecruiterId"] = recruiterId.ToString(),
            ["Requirements"] = requirements,
            ["Benefits"] = benefits,
        };
}
