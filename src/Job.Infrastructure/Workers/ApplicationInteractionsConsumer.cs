using System.Text.Json;
using Job.Core.Entities;
using Job.Infrastructure.Recommendations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharedKernel.Events;
using SharedKernel.Kafka;

namespace Job.Infrastructure.Workers;

/// <summary>
/// Feeds the recommendation read model from app-svc <c>application-events</c> (PBL6-10).
/// Every application event means "user applied to job", so submitted, status_changed and the
/// SRS name application.updated all upsert (applicant, job, Applied); status_changed fills the
/// row when the best-effort submitted publish was lost. Idempotent via <see cref="InteractionWriter"/>.
/// Uses its own consumer group (KAFKA_GROUP_JOB_RECOMMENDER), never the shared KAFKA_GROUP_ID.
/// </summary>
public class ApplicationInteractionsConsumer : KafkaConsumerService
{
    /// <summary>SRS 8.5 name for status changes; the shared contract uses application.status_changed.</summary>
    public const string LegacyUpdatedEventType = "application.updated";

    private readonly IConfiguration _configuration;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ApplicationInteractionsConsumer> _logger;

    public ApplicationInteractionsConsumer(
        IOptions<KafkaOptions> kafka,
        IConfiguration configuration,
        IServiceScopeFactory scopeFactory,
        ILogger<ApplicationInteractionsConsumer> logger)
        : base(kafka, logger)
    {
        _configuration = configuration;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override string Topic => Setting("KAFKA_TOPIC_APPLICATION_EVENTS", "application-events");

    protected override string GroupId => Setting("KAFKA_GROUP_JOB_RECOMMENDER", "job-svc-recommender");

    protected override async Task<MessageOutcome> HandleMessageAsync(string? key, string value, CancellationToken ct)
    {
        var eventType = TryGetEventType(value)?.Trim();
        if (eventType is not (ApplicationEventTypes.Submitted or ApplicationEventTypes.StatusChanged or LegacyUpdatedEventType))
        {
            _logger.LogWarning(
                "Skipping message on {Topic}: unsupported or missing eventType {EventType}.", Topic, eventType ?? "(none)");
            return MessageOutcome.Skip;
        }

        if (!TryParseEnvelope<ApplicationInteractionPayload>(value, out var envelope) || envelope is null)
        {
            _logger.LogWarning("Skipping message on {Topic}: cannot parse {EventType} payload.", Topic, eventType);
            return MessageOutcome.Skip;
        }

        var payload = envelope.Payload;
        if (payload.ApplicantId == Guid.Empty || payload.JobId == Guid.Empty || payload.OccurredAt == default)
        {
            _logger.LogWarning(
                "Skipping {EventType} {EventId} on {Topic}: empty applicantId/jobId or missing occurredAt.",
                eventType, envelope.EventId, Topic);
            return MessageOutcome.Skip;
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var writer = scope.ServiceProvider.GetRequiredService<InteractionWriter>();
            var inserted = await writer.UpsertAsync(
                payload.ApplicantId, payload.JobId, InteractionType.Applied,
                payload.ApplicationId, payload.OccurredAt, ct);
            if (inserted)
            {
                // The user's history changed: their cached ranking is stale.
                scope.ServiceProvider.GetService<RecommendationCache>()?.Invalidate(payload.ApplicantId);
            }

            _logger.LogDebug(
                "{EventType} {EventId}: interaction {Result} for job {JobId}.",
                eventType, envelope.EventId, inserted ? "stored" : "already present", payload.JobId);
            return MessageOutcome.Handled;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Transient (DB down, timeout): keep the offset and retry.
            _logger.LogError(ex, "Failed to store interaction for {EventType} {EventId}. Will retry.", eventType, envelope.EventId);
            return MessageOutcome.Retry;
        }
    }

    private string Setting(string key, string fallback)
    {
        var value = _configuration[key];
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static string? TryGetEventType(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.NameEquals("eventType") || property.NameEquals("EventType"))
                    return property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    /// <summary>Fields shared by every application event payload; other fields are ignored.</summary>
    private sealed record ApplicationInteractionPayload(
        Guid ApplicationId,
        Guid JobId,
        Guid ApplicantId,
        DateTime OccurredAt);
}
