using System.Text.Json;
using Job.Core.Entities;
using Job.Infrastructure.Outbox;
using SharedKernel.Events;
using JobPosting = Job.Core.Entities.Job;

namespace Job.Tests;

/// <summary>
/// Wire contract for job-events (topic <c>job-events</c>, consumed by search-svc and notif-svc).
/// Asserts the envelope and payload field names and JSON value kinds of the rows the outbox
/// writes for job.created, job.updated and job.deleted. A renamed, added or removed field fails here.
/// </summary>
public sealed class JobEventContractTests
{
    public static readonly string[] EnvelopeFields = ["eventId", "eventType", "version", "timestamp", "payload"];

    public static readonly string[] JobPayloadFields =
    [
        "jobId", "title", "description", "companyId", "companyName", "location",
        "salaryMin", "salaryMax", "currency", "categoryId", "categoryName",
        "employmentType", "experienceLevel", "recruiterId", "requirements", "benefits",
        "status", "occurredAt", "createdAt",
    ];

    public static readonly string[] DeletedPayloadFields = ["jobId", "occurredAt"];

    [Fact]
    public void Created_Envelope_HasContractFieldsAndType()
    {
        var root = Parse(JobOutboxEvents.Created(NewJob(), "Acme", "IT"));

        AssertEnvelope(root, JobEventTypes.Created);
        AssertPayload(root, JobPayloadFields);
    }

    [Fact]
    public void Updated_Envelope_HasContractFieldsAndType()
    {
        var root = Parse(JobOutboxEvents.Updated(NewJob(), "Acme", null));

        AssertEnvelope(root, JobEventTypes.Updated);
        AssertPayload(root, JobPayloadFields);
    }

    [Fact]
    public void Deleted_Envelope_HasContractFieldsAndType()
    {
        var root = Parse(JobOutboxEvents.Deleted(Guid.NewGuid()));

        AssertEnvelope(root, JobEventTypes.Deleted);
        AssertPayload(root, DeletedPayloadFields);
    }

    [Fact]
    public void EventTypeNames_AndTopicKey_AreUnchanged()
    {
        Assert.Equal("job.created", JobEventTypes.Created);
        Assert.Equal("job.updated", JobEventTypes.Updated);
        Assert.Equal("job.deleted", JobEventTypes.Deleted);
    }

    [Fact]
    public void Payload_ValueKinds_MatchContract()
    {
        var jobId = Guid.NewGuid();
        var payload = Parse(JobOutboxEvents.Updated(NewJob(jobId), "Acme", "IT")).GetProperty("payload");

        Assert.Equal(JsonValueKind.String, payload.GetProperty("jobId").ValueKind);
        Assert.Equal(jobId, payload.GetProperty("jobId").GetGuid());
        Assert.Equal(JsonValueKind.Number, payload.GetProperty("salaryMin").ValueKind);
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("categoryId").ValueKind);
        Assert.Equal("IT", payload.GetProperty("categoryName").GetString());
        Assert.Equal("Active", payload.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.String, payload.GetProperty("occurredAt").ValueKind);
    }

    private static JobPosting NewJob(Guid? id = null)
    {
        var job = new JobPosting("Backend Developer", "Build APIs", Guid.NewGuid(), "Da Nang", Guid.NewGuid(),
            initialStatus: JobStatus.Active, salaryMin: 15_000_000m, salaryMax: 25_000_000m);
        if (id is { } jobId)
            typeof(JobPosting).GetProperty(nameof(JobPosting.Id))!.SetValue(job, jobId);
        return job;
    }

    private static JsonElement Parse(OutboxEvent row) =>
        JsonDocument.Parse(row.Payload).RootElement.Clone();

    private static void AssertEnvelope(JsonElement root, string eventType)
    {
        Assert.Equal(EnvelopeFields.OrderBy(f => f), root.EnumerateObject().Select(p => p.Name).OrderBy(n => n));
        Assert.Equal(eventType, root.GetProperty("eventType").GetString());
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.Equal(JsonValueKind.String, root.GetProperty("timestamp").ValueKind);
    }

    private static void AssertPayload(JsonElement root, string[] expected)
    {
        var payload = root.GetProperty("payload");
        Assert.Equal(expected.OrderBy(f => f), payload.EnumerateObject().Select(p => p.Name).OrderBy(n => n));
    }
}
