using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Job.Api.DTOs;
using Job.Api.Endpoints;
using Job.Core.Entities;
using Job.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using SharedKernel.Events;
using SharedKernel.Kafka;

namespace Job.Tests.Integration;

/// <summary>
/// Job writes and their job.* outbox rows: the row is written by the same SaveChanges as the change,
/// a failed publish leaves the row for retry, and a failed save leaves no row.
/// </summary>
public class JobOutboxIntegrationTests
{
    private static readonly Guid Recruiter = Guid.NewGuid();

    [Fact]
    public async Task CreateJob_WritesJobAndOutboxRowInOneSave()
    {
        await using var host = await JobApiTestHost.StartAsync();
        var companyId = await host.CreateCompanyAsync(Recruiter, "Outbox Co");

        using var response = await host.SendAsync(HttpMethod.Post, "/api/jobs", Recruiter, "Recruiter",
            JobBody(companyId));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var jobId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var outbox = Assert.Single(await host.LoadOutboxAsync());
        Assert.Equal(JobEventTypes.Created, outbox.EventType);
        Assert.Equal(jobId.ToString(), outbox.AggregateId);
        Assert.False(outbox.IsPublished);
    }

    [Fact]
    public async Task UpdateJob_QueuesJobUpdatedRow()
    {
        await using var host = await JobApiTestHost.StartAsync();
        var companyId = await host.CreateCompanyAsync(Recruiter, "Outbox Co");
        var jobId = await CreateJobAsync(host, companyId);

        using var response = await host.SendAsync(HttpMethod.Put, $"/api/jobs/{jobId}", Recruiter, "Recruiter",
            JobBody(companyId, title: "Renamed"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rows = await host.LoadOutboxAsync();
        Assert.Equal([JobEventTypes.Created, JobEventTypes.Updated], rows.Select(r => r.EventType));
        Assert.Contains("\"title\":\"Renamed\"", rows[1].Payload);
    }

    [Fact]
    public async Task DeleteJob_QueuesJobDeletedRow()
    {
        await using var host = await JobApiTestHost.StartAsync();
        var companyId = await host.CreateCompanyAsync(Recruiter, "Outbox Co");
        var jobId = await CreateJobAsync(host, companyId);

        using var response = await host.SendAsync(HttpMethod.Delete, $"/api/jobs/{jobId}", Recruiter, "Recruiter");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var rows = await host.LoadOutboxAsync();
        Assert.Equal([JobEventTypes.Created, JobEventTypes.Deleted], rows.Select(r => r.EventType));
        Assert.All(rows, r => Assert.Equal(jobId.ToString(), r.AggregateId));
    }

    [Fact]
    public async Task FailedPublish_LeavesRowForRetry_AndPublishesOnRecovery()
    {
        await using var host = await JobApiTestHost.StartAsync();
        var companyId = await host.CreateCompanyAsync(Recruiter, "Outbox Co");
        await CreateJobAsync(host, companyId);
        var clock = new MutableClock(DateTime.UtcNow);

        Assert.False(await host.RunOutboxCycleAsync(new FailingProducer(), clock));

        var failed = Assert.Single(await host.LoadOutboxAsync());
        Assert.False(failed.IsPublished);
        Assert.Equal(1, failed.RetryCount);

        clock.Now = clock.Now.AddMinutes(1);
        var producer = new RecordingProducer();
        Assert.True(await host.RunOutboxCycleAsync(producer, clock));

        Assert.Equal(JobEventTypes.Created, producer.EventType);
        Assert.True(Assert.Single(await host.LoadOutboxAsync()).IsPublished);
    }

    [Fact]
    public async Task CreateJob_WhenSaveFails_LeavesNoJobAndNoOutboxRow()
    {
        var failing = new SaveChangesSwitch();
        var databaseName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<JobDbContext>()
            .UseInMemoryDatabase(databaseName)
            .AddInterceptors(failing)
            .Options;
        await using var db = new JobDbContext(options);
        var company = new Company("Rollback Co", Recruiter);
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        failing.FailOnSave = true;

        var dto = new JobCreateDto("Backend Developer", "Build APIs", company.Id, "Da Nang",
            null, null, null, null, null, null, null, null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => InvokeCreateJob(db, dto));

        Assert.Empty(await db.Jobs.IgnoreQueryFilters().ToListAsync());
        Assert.Empty(await db.OutboxEvents.ToListAsync());
    }

    [Fact]
    public async Task CreateJob_UsesOneSaveChangesCallForJobAndOutbox()
    {
        var counter = new SaveChangesSwitch();
        var options = new DbContextOptionsBuilder<JobDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .AddInterceptors(counter)
            .Options;
        await using var db = new JobDbContext(options);
        var company = new Company("Single Save Co", Recruiter);
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        counter.SaveCalls = 0;

        var dto = new JobCreateDto("Backend Developer", "Build APIs", company.Id, "Da Nang",
            null, null, null, null, null, null, null, null);
        var result = await InvokeCreateJob(db, dto);

        Assert.Equal(201, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.Equal(1, counter.SaveCalls);
        Assert.Single(await db.OutboxEvents.ToListAsync());
    }

    private static async Task<Guid> CreateJobAsync(JobApiTestHost host, Guid companyId)
    {
        using var response = await host.SendAsync(HttpMethod.Post, "/api/jobs", Recruiter, "Recruiter",
            JobBody(companyId));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static object JobBody(Guid companyId, string title = "Backend Developer") => new
    {
        title,
        description = "Build APIs",
        companyId,
        location = "Da Nang",
    };

    private static Task<IResult> InvokeCreateJob(JobDbContext db, JobCreateDto dto)
    {
        var ctx = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, Recruiter.ToString()),
                new Claim(ClaimTypes.Role, "Recruiter")
            ], "Test"))
        };
        var method = typeof(JobEndpoints).GetMethod("CreateJob", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (Task<IResult>)method.Invoke(null, [dto, db, ctx, CancellationToken.None])!;
    }

    /// <summary>Counts SaveChanges calls and can fail the next one before anything is written.</summary>
    private sealed class SaveChangesSwitch : SaveChangesInterceptor
    {
        public int SaveCalls { get; set; }
        public bool FailOnSave { get; set; }

        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        {
            BeforeSave();
            return result;
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            BeforeSave();
            return ValueTask.FromResult(result);
        }

        private void BeforeSave()
        {
            SaveCalls++;
            if (FailOnSave)
                throw new InvalidOperationException("Simulated database failure before commit.");
        }
    }

    private sealed class MutableClock(DateTime now) : TimeProvider
    {
        public DateTime Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(Now, DateTimeKind.Utc));
    }

    private sealed class FailingProducer : IKafkaProducer
    {
        public Task ProduceAsync<T>(string topic, string key, EventEnvelope<T> envelope, CancellationToken ct = default) =>
            throw new InvalidOperationException("Kafka unavailable.");

        public void Dispose()
        {
        }
    }

    private sealed class RecordingProducer : IKafkaProducer
    {
        public string? EventType { get; private set; }

        public Task ProduceAsync<T>(string topic, string key, EventEnvelope<T> envelope, CancellationToken ct = default)
        {
            EventType = envelope.EventType;
            return Task.CompletedTask;
        }

        public void Dispose()
        {
        }
    }
}
