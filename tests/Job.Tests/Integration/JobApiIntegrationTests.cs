using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Job.Core.Entities;

namespace Job.Tests.Integration;

/// <summary>
/// HTTP-level job API checks over TestServer + EF InMemory: public visibility, response contract,
/// company/job ownership (PR #53), and the internal service token.
/// </summary>
public class JobApiIntegrationTests
{
    private static readonly Guid OwnerRecruiter = Guid.NewGuid();
    private static readonly Guid OtherRecruiter = Guid.NewGuid();

    private static object JobBody(Guid companyId, string title = "Backend Developer") => new
    {
        title,
        description = "Build APIs",
        companyId,
        location = "Da Nang",
        salaryMin = 15_000_000,
        salaryMax = 25_000_000,
        experienceLevel = "Mid",
    };

    [Fact]
    public async Task PublicDetail_ActiveJob_HasContractFieldsAndNoRecruiterId()
    {
        await using var host = await JobApiTestHost.StartAsync();
        var companyId = await host.CreateCompanyAsync(OwnerRecruiter, "Own Corp");
        var jobId = await CreateJobAsync(host, OwnerRecruiter, companyId);

        using var response = await host.SendAsync(HttpMethod.Get, $"/api/jobs/{jobId}", null, null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JobContractTests.JobDetailFields.Order(), json.EnumerateObject().Select(p => p.Name).Order());
        Assert.False(json.TryGetProperty("recruiterId", out _));
        Assert.Equal("Active", json.GetProperty("status").GetString());
        Assert.Equal("Own Corp", json.GetProperty("companyName").GetString());
    }

    [Fact]
    public async Task RecruiterList_ItemsHaveContractFieldsAndNoRecruiterId()
    {
        await using var host = await JobApiTestHost.StartAsync();
        var companyId = await host.CreateCompanyAsync(OwnerRecruiter, "Own Corp");
        await CreateJobAsync(host, OwnerRecruiter, companyId);

        using var response = await host.SendAsync(HttpMethod.Get, "/api/jobs/recruiter", OwnerRecruiter, "Recruiter");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JobContractTests.PaginatedFields.Order(), json.EnumerateObject().Select(p => p.Name).Order());
        var item = json.GetProperty("items")[0];
        Assert.Equal(JobContractTests.JobDetailFields.Order(), item.EnumerateObject().Select(p => p.Name).Order());
    }

    [Theory]
    [InlineData(JobStatus.Pending)]
    [InlineData(JobStatus.Rejected)]
    public async Task PublicDetail_PendingOrRejectedJob_Returns404(JobStatus status)
    {
        await using var host = await JobApiTestHost.StartAsync();
        var companyId = await host.CreateCompanyAsync(OwnerRecruiter, "Own Corp");
        var jobId = await host.SeedJobAsync(companyId, OwnerRecruiter, status);

        using var response = await host.SendAsync(HttpMethod.Get, $"/api/jobs/{jobId}", null, null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PublicDetail_DeletedJob_Returns404()
    {
        await using var host = await JobApiTestHost.StartAsync();
        var companyId = await host.CreateCompanyAsync(OwnerRecruiter, "Own Corp");
        var jobId = await CreateJobAsync(host, OwnerRecruiter, companyId);
        using (var del = await host.SendAsync(HttpMethod.Delete, $"/api/jobs/{jobId}", OwnerRecruiter, "Recruiter"))
            Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        using var response = await host.SendAsync(HttpMethod.Get, $"/api/jobs/{jobId}", null, null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CreateJob_ReturnsCreatedWithIdAndMessage()
    {
        await using var host = await JobApiTestHost.StartAsync();
        var companyId = await host.CreateCompanyAsync(OwnerRecruiter, "Own Corp");

        using var response = await host.SendAsync(HttpMethod.Post, "/api/jobs", OwnerRecruiter, "Recruiter",
            JobBody(companyId));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { "id", "message" }, json.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(JsonValueKind.String, json.GetProperty("id").ValueKind);
        Assert.Equal("Job created", json.GetProperty("message").GetString());
    }

    [Fact]
    public async Task CreateJob_ForCompanyOwnedByAnotherRecruiter_Returns403AndCreatesNothing()
    {
        await using var host = await JobApiTestHost.StartAsync();
        var otherCompanyId = await host.CreateCompanyAsync(OtherRecruiter, "Other Corp");

        using var response = await host.SendAsync(HttpMethod.Post, "/api/jobs", OwnerRecruiter, "Recruiter",
            JobBody(otherCompanyId));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var listed = await host.SendAsync(HttpMethod.Get, "/api/jobs/recruiter", OwnerRecruiter, "Recruiter");
        var json = await listed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, json.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task UpdateJob_MoveToCompanyOwnedByAnotherRecruiter_Returns403AndKeepsCompany()
    {
        await using var host = await JobApiTestHost.StartAsync();
        var ownCompanyId = await host.CreateCompanyAsync(OwnerRecruiter, "Own Corp");
        var otherCompanyId = await host.CreateCompanyAsync(OtherRecruiter, "Other Corp");
        var jobId = await CreateJobAsync(host, OwnerRecruiter, ownCompanyId);

        using var response = await host.SendAsync(HttpMethod.Put, $"/api/jobs/{jobId}", OwnerRecruiter, "Recruiter",
            JobBody(otherCompanyId, "Moved"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var job = await host.LoadJobAsync(jobId);
        Assert.Equal(ownCompanyId, job!.CompanyId);
        Assert.Equal("Backend Developer", job.Title);
    }

    [Fact]
    public async Task UpdateJob_ByAnotherRecruiterOfOwnCompany_Returns403()
    {
        await using var host = await JobApiTestHost.StartAsync();
        var companyId = await host.CreateCompanyAsync(OwnerRecruiter, "Own Corp");
        var jobId = await CreateJobAsync(host, OwnerRecruiter, companyId);

        using var response = await host.SendAsync(HttpMethod.Put, $"/api/jobs/{jobId}", OtherRecruiter, "Recruiter",
            JobBody(companyId));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task UpdateJob_OwnCompany_Returns200WithMessage()
    {
        await using var host = await JobApiTestHost.StartAsync();
        var companyId = await host.CreateCompanyAsync(OwnerRecruiter, "Own Corp");
        var jobId = await CreateJobAsync(host, OwnerRecruiter, companyId);

        using var response = await host.SendAsync(HttpMethod.Put, $"/api/jobs/{jobId}", OwnerRecruiter, "Recruiter",
            JobBody(companyId, "Senior Backend Developer"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Job updated", json.GetProperty("message").GetString());
        Assert.Equal("Senior Backend Developer", (await host.LoadJobAsync(jobId))!.Title);
    }

    [Fact]
    public async Task Internal_MissingOrWrongToken_Returns401()
    {
        await using var host = await JobApiTestHost.StartAsync();
        var companyId = await host.CreateCompanyAsync(OwnerRecruiter, "Own Corp");
        var jobId = await host.SeedJobAsync(companyId, OwnerRecruiter, JobStatus.Pending);

        using var none = await host.SendAsync(HttpMethod.Get, $"/api/internal/jobs/{jobId}", null, null);
        using var wrong = await SendWithAuthAsync(host, jobId, "Bearer not-the-token");

        Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
    }

    [Fact]
    public async Task Internal_ValidToken_ReturnsPendingJobStatusAndCompany()
    {
        await using var host = await JobApiTestHost.StartAsync();
        var companyId = await host.CreateCompanyAsync(OwnerRecruiter, "Own Corp");
        var jobId = await host.SeedJobAsync(companyId, OwnerRecruiter, JobStatus.Pending);

        using var response = await SendWithAuthAsync(host, jobId, $"Bearer {JobApiTestHost.InternalToken}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Pending", json.GetProperty("status").GetString());
        Assert.Equal(companyId, json.GetProperty("companyId").GetGuid());
    }

    private static Task<HttpResponseMessage> SendWithAuthAsync(JobApiTestHost host, Guid jobId, string authorization)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/internal/jobs/{jobId}");
        request.Headers.TryAddWithoutValidation("Authorization", authorization);
        return host.Client.SendAsync(request);
    }

    private static async Task<Guid> CreateJobAsync(JobApiTestHost host, Guid recruiterId, Guid companyId)
    {
        using var response = await host.SendAsync(HttpMethod.Post, "/api/jobs", recruiterId, "Recruiter",
            JobBody(companyId));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }
}
