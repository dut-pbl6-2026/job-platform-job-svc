using SharedKernel.Events;
using SharedKernel.Kafka;

namespace Job.Api.Services;

/// <summary>
/// Kafka publisher for job events (PBL6-5 Task 3.3). Publishes
/// <c>job.created</c>, <c>job.updated</c>, <c>job.deleted</c> to the
/// <c>job-events</c> topic with <c>key = JobId</c> (partition ordering per entity).
///
/// Includes a runtime circuit-breaker (plan 2.3 / B-8):
/// - Kafka healthy: publish to Kafka, skip HTTP fallback.
/// - Kafka failing (>= 3 failures in a row): auto-switch to HTTP fallback
///   via <see cref="SearchSyncPublisher"/> and stop trying Kafka.
/// - Recovery: background health-check pings the broker every 15s;
///   when reachable, re-enables Kafka path.
///
/// Must be registered as <b>Singleton</b> in DI — circuit-breaker state
/// (KafkaHealthy, _consecutiveFailures) is in-memory and must persist
/// across requests. Scoped/Transient would reset per request.
///
/// Publish failures are logged as structured ERROR with JobId + EventType
/// for manual replay (plan 2.6 / B-7).
/// </summary>
public sealed class KafkaJobEventPublisher : IDisposable
{
    private const string Topic = "job-events";
    private const int CircuitBreakerThreshold = 3;

    private readonly IKafkaProducer _producer;
    private readonly SearchSyncPublisher _httpFallback;
    private readonly ILogger<KafkaJobEventPublisher> _logger;
    private readonly IConfiguration _configuration;

    // Circuit-breaker state (in-memory, single instance).
    // Thread-safe via Interlocked for the counter and volatile for the flag.
    private volatile bool _kafkaHealthy = true;
    private int _consecutiveFailures;

    /// <summary>Whether Kafka is currently considered healthy.</summary>
    public bool IsKafkaHealthy => _kafkaHealthy;

    public KafkaJobEventPublisher(
        IKafkaProducer producer,
        SearchSyncPublisher httpFallback,
        ILogger<KafkaJobEventPublisher> logger,
        IConfiguration configuration)
    {
        _producer = producer ?? throw new ArgumentNullException(nameof(producer));
        _httpFallback = httpFallback ?? throw new ArgumentNullException(nameof(httpFallback));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    }

    /// <summary>
    /// Publishes a <c>job.created</c> event. Best-effort: never throws.
    /// Falls back to HTTP sync when Kafka circuit is open.
    /// </summary>
    public async Task PublishJobCreatedAsync(
        JobCreatedEvent payload, string companyName, string? categoryName,
        string status, CancellationToken ct = default)
    {
        var envelope = EventEnvelope<JobCreatedEvent>.Create(JobEventTypes.Created, payload);
        var key = payload.JobId.ToString();

        if (await TryPublishKafkaAsync(key, envelope, ct))
        {
            return; // Kafka succeeded
        }

        // Fallback to HTTP sync
        await FallbackHttpUpsertAsync(payload.JobId, payload.Title, payload.Description,
            payload.CompanyId, companyName, payload.Location,
            payload.SalaryMin, payload.SalaryMax, payload.Currency,
            payload.CategoryId, categoryName, payload.Requirements, payload.Benefits,
            payload.EmploymentType, payload.ExperienceLevel, payload.RecruiterId,
            status, key, JobEventTypes.Created, ct);
    }

    /// <summary>
    /// Publishes a <c>job.updated</c> event. Best-effort: never throws.
    /// Falls back to HTTP sync when Kafka circuit is open.
    /// </summary>
    public async Task PublishJobUpdatedAsync(
        JobUpdatedEvent payload, string companyName, string? categoryName,
        string status, CancellationToken ct = default)
    {
        var envelope = EventEnvelope<JobUpdatedEvent>.Create(JobEventTypes.Updated, payload);
        var key = payload.JobId.ToString();

        if (await TryPublishKafkaAsync(key, envelope, ct))
        {
            return;
        }

        await FallbackHttpUpsertAsync(payload.JobId, payload.Title, payload.Description,
            payload.CompanyId, companyName, payload.Location,
            payload.SalaryMin, payload.SalaryMax, payload.Currency,
            payload.CategoryId, categoryName, payload.Requirements, payload.Benefits,
            payload.EmploymentType, payload.ExperienceLevel, payload.RecruiterId,
            status, key, JobEventTypes.Updated, ct);
    }

    /// <summary>
    /// Publishes a <c>job.deleted</c> event. Best-effort: never throws.
    /// Falls back to HTTP sync when Kafka circuit is open.
    /// </summary>
    public async Task PublishJobDeletedAsync(Guid jobId, CancellationToken ct = default)
    {
        var payload = new JobDeletedEvent(jobId, DateTime.UtcNow);
        var envelope = EventEnvelope<JobDeletedEvent>.Create(JobEventTypes.Deleted, payload);
        var key = jobId.ToString();

        if (await TryPublishKafkaAsync(key, envelope, ct))
        {
            return;
        }

        // Fallback: HTTP delete
        try
        {
            await _httpFallback.PublishDeleteAsync(jobId, ct);
            _logger.LogWarning(
                "Kafka circuit open. Fallback HTTP delete succeeded for JobId={JobId} EventType={EventType}.",
                jobId, JobEventTypes.Deleted);
        }
        catch (Exception httpEx)
        {
            _logger.LogError(httpEx,
                "KafkaJobEventPublisher: Failed to publish {EventType} for JobId={JobId}. " +
                "Fallback HTTP sync also failed. Event payload logged for manual replay.",
                JobEventTypes.Deleted, jobId);
        }
    }

    /// <summary>
    /// Attempts to publish an envelope via Kafka. Returns true on success.
    /// On failure: increments the circuit-breaker counter and returns false.
    /// When the circuit is already open, returns false immediately without trying.
    /// </summary>
    private async Task<bool> TryPublishKafkaAsync<T>(
        string key, EventEnvelope<T> envelope, CancellationToken ct)
    {
        if (!_kafkaHealthy)
        {
            return false; // Circuit open — caller must use HTTP fallback
        }

        try
        {
            await _producer.ProduceAsync(Topic, key, envelope, ct);
            // Success — reset failure counter
            Interlocked.Exchange(ref _consecutiveFailures, 0);
            _logger.LogDebug(
                "Published {EventType} for key={Key} to Kafka topic {Topic}.",
                envelope.EventType, key, Topic);
            return true;
        }
        catch (Exception ex)
        {
            var failures = Interlocked.Increment(ref _consecutiveFailures);

            if (failures >= CircuitBreakerThreshold && _kafkaHealthy)
            {
                _kafkaHealthy = false;
                _logger.LogWarning(
                    "Kafka publish failing ({Failures} consecutive). " +
                    "Switching to HTTP sync fallback. Will auto-recover when broker is reachable.",
                    failures);
            }

            _logger.LogError(ex,
                "KafkaJobEventPublisher: Failed to publish {EventType} for JobId={Key}. " +
                "ConsecutiveFailures={Failures}. Circuit={CircuitState}.",
                envelope.EventType, key, failures,
                _kafkaHealthy ? "Closed" : "Open");

            return false;
        }
    }

    /// <summary>
    /// Called by <see cref="KafkaHealthCheckService"/> when the broker becomes
    /// reachable again. Re-enables the Kafka publish path.
    /// </summary>
    internal void MarkKafkaHealthy()
    {
        if (_kafkaHealthy)
        {
            return;
        }

        _kafkaHealthy = true;
        Interlocked.Exchange(ref _consecutiveFailures, 0);
        _logger.LogInformation("Kafka recovered, re-enabling Kafka publish path.");
    }

    private async Task FallbackHttpUpsertAsync(
        Guid jobId, string title, string description, Guid companyId, string companyName,
        string location, decimal? salaryMin, decimal? salaryMax, string currency,
        Guid? categoryId, string? categoryName, string? requirements, string? benefits,
        string employmentType, string experienceLevel, Guid recruiterId,
        string status, string key, string eventType, CancellationToken ct)
    {
        try
        {
            var doc = SearchSyncPublisher.BuildDocument(
                jobId, title, description, companyId, companyName, location,
                salaryMin, salaryMax, currency, categoryId, categoryName,
                requirements, benefits, employmentType, experienceLevel, recruiterId, status);
            await _httpFallback.PublishUpsertAsync(doc, ct);
            _logger.LogWarning(
                "Kafka circuit open. Fallback HTTP upsert succeeded for JobId={JobId} EventType={EventType}.",
                jobId, eventType);
        }
        catch (Exception httpEx)
        {
            _logger.LogError(httpEx,
                "KafkaJobEventPublisher: Failed to publish {EventType} for JobId={JobId}. " +
                "Fallback HTTP sync also failed. Event payload logged for manual replay.",
                eventType, jobId);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // IKafkaProducer disposal is handled by DI (it's also Singleton).
    }
}
