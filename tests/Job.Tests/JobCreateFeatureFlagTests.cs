using System.Reflection;
using System.Security.Claims;
using Job.Api.DTOs;
using Job.Api.Endpoints;
using Job.Api.Services;
using Job.Core.Entities;
using Job.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SharedKernel.Events;
using SharedKernel.Kafka;

namespace Job.Tests;

/// <summary>
/// Verifies the application-layer FEATURE_ADMIN_APPROVAL decision (MAINT-01):
/// the domain entity never reads configuration, the endpoint picks the initial status.
/// </summary>
public sealed class JobCreateFeatureFlagTests : IDisposable
{
    private readonly JobDbContext _db;
    private readonly Guid _companyId = Guid.NewGuid();
    private readonly Guid _recruiterId = Guid.NewGuid();

    public JobCreateFeatureFlagTests()
    {
        var options = new DbContextOptionsBuilder<JobDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new JobDbContext(options);
        _db.Database.EnsureCreated();

        var company = new Company("Feature Flag Corp", _recruiterId);
        typeof(Company).GetProperty(nameof(Company.Id))!.SetValue(company, _companyId);
        _db.Companies.Add(company);
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    [Theory]
    [InlineData("true", JobStatus.Pending)]
    [InlineData("false", JobStatus.Active)]
    [InlineData(null, JobStatus.Active)]
    public async Task CreateJob_FeatureFlag_ControlsInitialStatus(
        string? flagValue, JobStatus expectedStatus)
    {
        var configuration = BuildConfiguration(flagValue);
        using var http = new HttpClient();
        var searchSync = new SearchSyncPublisher(
            http, configuration, NullLogger<SearchSyncPublisher>.Instance);
        using var kafka = new KafkaJobEventPublisher(
            new RecordingProducer(), searchSync,
            NullLogger<KafkaJobEventPublisher>.Instance, configuration);

        var dto = new JobCreateDto(
            "Backend Developer", "Build APIs", _companyId, "Da Nang",
            null, null, null, null, null, null, null, null);

        var result = await InvokeCreateJob(dto, searchSync, kafka, configuration);

        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(201, status.StatusCode);
        var job = await _db.Jobs.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(expectedStatus, job.Status);
    }

    private static IConfiguration BuildConfiguration(string? flagValue)
    {
        var values = new Dictionary<string, string?>();
        if (flagValue is not null)
        {
            values["FEATURE_ADMIN_APPROVAL"] = flagValue;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private HttpContext BuildContext() =>
        new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, _recruiterId.ToString()),
                new Claim(ClaimTypes.Role, "Recruiter")
            ], "Test"))
        };

    private async Task<IResult> InvokeCreateJob(
        JobCreateDto dto,
        SearchSyncPublisher searchSync,
        KafkaJobEventPublisher kafka,
        IConfiguration configuration)
    {
        var method = typeof(JobEndpoints).GetMethod(
            "CreateJob", BindingFlags.NonPublic | BindingFlags.Static)!;
        return await (Task<IResult>)method.Invoke(
            null,
            [dto, _db, BuildContext(), searchSync, kafka, configuration, CancellationToken.None])!;
    }

    private sealed class RecordingProducer : IKafkaProducer
    {
        public Task ProduceAsync<T>(
            string topic, string key, EventEnvelope<T> envelope,
            CancellationToken ct = default) => Task.CompletedTask;

        public void Dispose()
        {
        }
    }
}
