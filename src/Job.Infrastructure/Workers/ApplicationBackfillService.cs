using Job.Core.Entities;
using Job.Infrastructure.Clients;
using Job.Infrastructure.Recommendations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Job.Infrastructure.Workers;

/// <summary>Summary of one backfill run.</summary>
/// <param name="Completed">True when every page was read.</param>
public sealed record BackfillReport(bool Completed, int Pages, int Inserted, int InvalidItems);

/// <summary>
/// One-time startup backfill of <c>JobInteractions</c> from app-svc (PBL6-10): applications
/// older than Kafka retention never reach the consumer. Runs only when
/// RECOMMENDATION_BACKFILL_ENABLED=true and APP_SERVICE_URL + INTERNAL_SERVICE_TOKEN are set.
/// Offset paging is safe because app-svc orders by CreatedAt, Id and never deletes applications:
/// new rows only append. Duplicates are absorbed by the unique index. Never fails startup.
/// </summary>
public sealed class ApplicationBackfillService : BackgroundService
{
    public const int PageSize = 100;
    public const int MaxPages = 1_000;
    public const int MaxRetries = 3;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RecommendationOptions _options;
    private readonly ILogger<ApplicationBackfillService> _logger;

    public ApplicationBackfillService(
        IServiceScopeFactory scopeFactory,
        IOptions<RecommendationOptions> options,
        ILogger<ApplicationBackfillService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>First retry delay; doubles on each retry (1s, 2s, 4s).</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RunAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // Recommendations degrade to content/popular; the service keeps running.
            _logger.LogError(ex, "Recommendation backfill failed.");
        }
    }

    public async Task<BackfillReport> RunAsync(CancellationToken ct)
    {
        if (!_options.BackfillEnabled)
        {
            _logger.LogInformation("Recommendation backfill disabled (RECOMMENDATION_BACKFILL_ENABLED is not true).");
            return new BackfillReport(false, 0, 0, 0);
        }

        if (_options.AppServiceUrl is null || string.IsNullOrWhiteSpace(_options.InternalServiceToken))
        {
            _logger.LogWarning(
                "Recommendation backfill enabled but APP_SERVICE_URL or INTERNAL_SERVICE_TOKEN is not set. Skipping.");
            return new BackfillReport(false, 0, 0, 0);
        }

        var (pages, inserted, invalid) = (0, 0, 0);
        for (var page = 0; page < MaxPages; page++)
        {
            var result = await FetchWithRetryAsync(page, ct);
            if (result.Status != PageFetchStatus.Ok || result.Page is null)
            {
                _logger.LogError(
                    "Recommendation backfill stopped at page {Page}: {Status} ({Detail}). Inserted {Inserted} interactions.",
                    page, result.Status, result.Detail, inserted);
                return new BackfillReport(false, pages, inserted, invalid);
            }

            pages++;
            invalid += result.Page.InvalidItems;
            if (result.Page.InvalidItems > 0)
            {
                _logger.LogWarning(
                    "Recommendation backfill page {Page}: skipped {Invalid} invalid items.", page, result.Page.InvalidItems);
            }

            inserted += await StoreAsync(result.Page.Items, ct);

            var emptyPage = result.Page.Items.Count == 0 && result.Page.InvalidItems == 0;
            if (emptyPage || page >= result.Page.TotalPages - 1)
            {
                _logger.LogInformation(
                    "Recommendation backfill done: {Pages} pages, {Inserted} interactions inserted, {Invalid} invalid items skipped.",
                    pages, inserted, invalid);
                return new BackfillReport(true, pages, inserted, invalid);
            }
        }

        _logger.LogWarning(
            "Recommendation backfill hit the safety cap of {MaxPages} pages. Inserted {Inserted} interactions.",
            MaxPages, inserted);
        return new BackfillReport(false, pages, inserted, invalid);
    }

    private async Task<PageFetchResult> FetchWithRetryAsync(int page, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<AppServiceClient>();
        var delay = RetryDelay;
        for (var attempt = 0; ; attempt++)
        {
            var result = await client.GetApplicationsAsync(page, PageSize, ct);
            if (result.Status != PageFetchStatus.Transient || attempt >= MaxRetries)
                return result;

            _logger.LogWarning(
                "Recommendation backfill page {Page} failed ({Detail}); retry {Attempt}/{MaxRetries} in {Delay}.",
                page, result.Detail, attempt + 1, MaxRetries, delay);
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, ct);
            delay *= 2;
        }
    }

    private async Task<int> StoreAsync(IReadOnlyList<ExportedApplication> items, CancellationToken ct)
    {
        if (items.Count == 0)
            return 0;
        using var scope = _scopeFactory.CreateScope();
        var writer = scope.ServiceProvider.GetRequiredService<InteractionWriter>();
        var inserted = 0;
        foreach (var item in items)
        {
            if (await writer.UpsertAsync(item.ApplicantId, item.JobId, InteractionType.Applied, item.Id, item.CreatedAt, ct))
                inserted++;
        }

        return inserted;
    }
}
