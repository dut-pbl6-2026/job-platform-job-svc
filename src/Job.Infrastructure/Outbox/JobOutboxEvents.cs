using System.Text.Json;
using Job.Core.Entities;
using SharedKernel.Events;
using SharedKernel.Kafka;
using JobPosting = Job.Core.Entities.Job;

namespace Job.Infrastructure.Outbox;

/// <summary>
/// Builds the outbox rows for job.created / job.updated / job.deleted. Every job write adds one of these
/// to the same <c>SaveChanges</c> as the change, so the event commits with the change or not at all.
/// The payload is serialized with <see cref="KafkaJson.Options"/>, the same bytes the producer writes.
/// </summary>
public static class JobOutboxEvents
{
    public static OutboxEvent Created(JobPosting job, string companyName, string? categoryName)
    {
        var payload = new JobCreatedEvent(
            job.Id, job.Title, job.Description, job.CompanyId, companyName,
            job.Location, job.SalaryMin, job.SalaryMax, job.SalaryCurrency,
            job.CategoryId, categoryName, job.EmploymentType, job.ExperienceLevel,
            job.RecruiterId, job.Requirements, job.Benefits,
            job.Status.ToString(), DateTime.UtcNow, job.CreatedAt);
        return Build(JobEventTypes.Created, payload, job.Id);
    }

    /// <summary>
    /// Also used for approve and reject: the status changes there, and the event carries the real status.
    /// </summary>
    public static OutboxEvent Updated(JobPosting job, string companyName, string? categoryName)
    {
        var payload = new JobUpdatedEvent(
            job.Id, job.Title, job.Description, job.CompanyId, companyName,
            job.Location, job.SalaryMin, job.SalaryMax, job.SalaryCurrency,
            job.CategoryId, categoryName, job.EmploymentType, job.ExperienceLevel,
            job.RecruiterId, job.Requirements, job.Benefits,
            job.Status.ToString(), DateTime.UtcNow, job.CreatedAt);
        return Build(JobEventTypes.Updated, payload, job.Id);
    }

    public static OutboxEvent Deleted(Guid jobId) =>
        Build(JobEventTypes.Deleted, new JobDeletedEvent(jobId, DateTime.UtcNow), jobId);

    private static OutboxEvent Build<T>(string eventType, T payload, Guid aggregateId) =>
        new(
            eventType,
            JsonSerializer.Serialize(EventEnvelope<T>.Create(eventType, payload), KafkaJson.Options),
            aggregateId.ToString());
}
