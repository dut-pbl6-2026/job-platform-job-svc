using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Job.Infrastructure.Recommendations;
using Microsoft.Extensions.Options;

namespace Job.Infrastructure.Clients;

/// <summary>One exported application: who applied to which job, and when (UTC).</summary>
public sealed record ExportedApplication(Guid Id, Guid JobId, Guid ApplicantId, DateTime CreatedAt);

/// <summary>A validated page: valid items, how many items were rejected, and the page count.</summary>
public sealed record ExportedApplicationsPage(IReadOnlyList<ExportedApplication> Items, int InvalidItems, int TotalPages);

/// <summary>Outcome of one page request.</summary>
public enum PageFetchStatus
{
    Ok,

    /// <summary>Network error, timeout or 5xx: worth retrying.</summary>
    Transient,

    /// <summary>401/403 or another 4xx: retrying will not help.</summary>
    Rejected,

    /// <summary>200 but the body is not the expected envelope.</summary>
    InvalidShape,
}

public sealed record PageFetchResult(PageFetchStatus Status, ExportedApplicationsPage? Page, string? Detail);

/// <summary>
/// Client for app-svc <c>GET /api/internal/applications</c> (service → service, PBL6-10).
/// The response is untrusted input: the envelope and every item are validated here.
/// </summary>
public sealed class AppServiceClient
{
    private readonly HttpClient _http;
    private readonly RecommendationOptions _options;

    public AppServiceClient(HttpClient http, IOptions<RecommendationOptions> options)
    {
        _http = http;
        _options = options.Value;
    }

    public async Task<PageFetchResult> GetApplicationsAsync(int page, int size, CancellationToken ct)
    {
        var baseUrl = _options.AppServiceUrl?.TrimEnd('/')
                      ?? throw new InvalidOperationException("APP_SERVICE_URL is not configured.");
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{baseUrl}/api/internal/applications?page={page.ToString(CultureInfo.InvariantCulture)}&size={size.ToString(CultureInfo.InvariantCulture)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.InternalServiceToken);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            return new PageFetchResult(PageFetchStatus.Transient, null, ex.Message);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new PageFetchResult(PageFetchStatus.Transient, null, "timeout");
        }

        using (response)
        {
            if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.RequestTimeout)
                return new PageFetchResult(PageFetchStatus.Transient, null, $"HTTP {(int)response.StatusCode}");
            if (!response.IsSuccessStatusCode)
                return new PageFetchResult(PageFetchStatus.Rejected, null, $"HTTP {(int)response.StatusCode}");

            var body = await response.Content.ReadAsStringAsync(ct);
            var parsed = Parse(body);
            return parsed is null
                ? new PageFetchResult(PageFetchStatus.InvalidShape, null, "unexpected response shape")
                : new PageFetchResult(PageFetchStatus.Ok, parsed, null);
        }
    }

    /// <summary>Returns null when the envelope is wrong; invalid items are counted, not fatal.</summary>
    private static ExportedApplicationsPage? Parse(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array
                || !root.TryGetProperty("totalPages", out var totalPagesElement)
                || !totalPagesElement.TryGetInt32(out var totalPages) || totalPages < 0)
            {
                return null;
            }

            var valid = new List<ExportedApplication>();
            var invalid = 0;
            foreach (var item in items.EnumerateArray())
            {
                if (TryReadItem(item, out var application))
                    valid.Add(application);
                else
                    invalid++;
            }

            return new ExportedApplicationsPage(valid, invalid, totalPages);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryReadItem(JsonElement item, out ExportedApplication application)
    {
        application = null!;
        if (item.ValueKind != JsonValueKind.Object
            || !TryGuid(item, "id", out var id)
            || !TryGuid(item, "jobId", out var jobId)
            || !TryGuid(item, "applicantId", out var applicantId)
            || !item.TryGetProperty("createdAt", out var createdAtElement)
            || createdAtElement.ValueKind != JsonValueKind.String
            || !DateTime.TryParse(
                createdAtElement.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var createdAt))
        {
            return false;
        }

        application = new ExportedApplication(id, jobId, applicantId, DateTime.SpecifyKind(createdAt, DateTimeKind.Utc));
        return true;
    }

    private static bool TryGuid(JsonElement item, string name, out Guid value)
    {
        value = Guid.Empty;
        return item.TryGetProperty(name, out var element)
               && element.ValueKind == JsonValueKind.String
               && Guid.TryParse(element.GetString(), out value)
               && value != Guid.Empty;
    }
}
