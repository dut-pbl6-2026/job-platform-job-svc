using SharedKernel.Events;
using SharedKernel.Kafka;
using JobPosting = Job.Core.Entities.Job;

namespace Job.Api.Services;

/// <summary>
/// Publishes job lifecycle events to the <c>job-events</c> Kafka topic
/// (PBL6-34, SRS KAFKA-01-01 / KAFKA-01-03).
/// Best-effort: never fails job CRUD and never propagates exceptions
/// (including cancellation) to callers. Kafka key is always the JobId so
/// one job stays ordered on one partition.
/// </summary>
public class JobEventPublisher
{
    private readonly IKafkaProducer _producer;
    private readonly string _topic;
    private readonly ILogger<JobEventPublisher> _logger;

    public JobEventPublisher(IKafkaProducer producer, IConfiguration configuration, ILogger<JobEventPublisher> logger)
    {
        _producer = producer;
        _topic = (configuration["KAFKA_TOPIC_JOB_EVENTS"]
            ?? configuration["Kafka:Topic"]
            ?? "job-events").Trim();
        if (string.IsNullOrWhiteSpace(_topic))
        {
            _topic = "job-events";
        }

        _logger = logger;
    }

    public async Task PublishCreatedAsync(JobPosting job, string companyName, string? categoryName, CancellationToken ct = default)
    {
        var payload = new JobCreatedEvent(
            job.Id, job.Title, job.Description, job.CompanyId, companyName,
            job.Location, job.SalaryMin, job.SalaryMax, job.SalaryCurrency,
            job.CategoryId, categoryName, job.EmploymentType, job.ExperienceLevel,
            job.RecruiterId, job.Requirements, job.Benefits,
            job.Status.ToString(), DateTime.UtcNow);
        await ProduceAsync(JobEventTypes.Created, job.Id.ToString(), EventEnvelope<JobCreatedEvent>.Create(JobEventTypes.Created, payload));
    }

    public async Task PublishUpdatedAsync(JobPosting job, string companyName, string? categoryName, CancellationToken ct = default)
    {
        var payload = new JobUpdatedEvent(
            job.Id, job.Title, job.Description, job.CompanyId, companyName,
            job.Location, job.SalaryMin, job.SalaryMax, job.SalaryCurrency,
            job.CategoryId, categoryName, job.EmploymentType, job.ExperienceLevel,
            job.RecruiterId, job.Requirements, job.Benefits,
            job.Status.ToString(), DateTime.UtcNow);
        await ProduceAsync(JobEventTypes.Updated, job.Id.ToString(), EventEnvelope<JobUpdatedEvent>.Create(JobEventTypes.Updated, payload));
    }

    public async Task PublishDeletedAsync(Guid jobId, CancellationToken ct = default)
    {
        var payload = new JobDeletedEvent(jobId, DateTime.UtcNow);
        await ProduceAsync(JobEventTypes.Deleted, jobId.ToString(), EventEnvelope<JobDeletedEvent>.Create(JobEventTypes.Deleted, payload));
    }

    private async Task ProduceAsync<T>(string eventType, string key, EventEnvelope<T> envelope)
    {
        try
        {
            await _producer.ProduceAsync(_topic, key, envelope, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Kafka publish failed (non-blocking) {EventType} key={Key} topic={Topic}.", eventType, key, _topic);
        }
    }
}
