using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Job.Core.Entities;

namespace Job.Tests.Integration;

/// <summary>
/// HTTP-level checks for the product rules: jobs publish instantly, web admin moderation can take a job
/// down with a reason and restore it, and the company surface exposes its public job list and the
/// caller's company-admin flag.
/// </summary>
public class ModerationAndCompanyHttpTests
{
    private static readonly Guid OwnerRecruiter = Guid.NewGuid();
    private static readonly Guid MemberRecruiter = Guid.NewGuid();
    private static readonly Guid AdminId = Guid.NewGuid();

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
    public async Task CreateJob_PublishesImmediately_AndIsVisibleToThePublic()
    {
        await using var host = await JobApiTestHost.StartAsync();
        var companyId = await host.CreateCompanyAsync(OwnerRecruiter, "Instant Corp");

        var jobId = await CreateJobAsync(host, companyId);

        using var publicRead = await host.SendAsync(HttpMethod.Get, $"/api/jobs/{jobId}", null, null);
        Assert.Equal(HttpStatusCode.OK, publicRead.StatusCode);
        var json = await publicRead.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Active", json.GetProperty("status").GetString());

        // job.created already carries Active, so search-svc indexes it without an approval step.
        var created = Assert.Single(await host.LoadOutboxAsync());
        Assert.Equal(SharedKernel.Events.JobEventTypes.Created, created.EventType);
        Assert.Contains("\"status\":\"Active\"", created.Payload);
    }

    [Fact]
    public async Task Takedown_HidesTheJobPersistsTheReason_AndRestoreBringsItBack()
    {
        await using var host = await JobApiTestHost.StartAsync();
        var companyId = await host.CreateCompanyAsync(OwnerRecruiter, "Moderated Corp");
        var jobId = await CreateJobAsync(host, companyId);

        // Takedown with a required reason.
        using var takedown = await host.SendAsync(
            HttpMethod.Post, $"/api/admin/jobs/{jobId}/takedown", AdminId, "Admin",
            new { reason = "  Misleading salary  " });
        Assert.Equal(HttpStatusCode.OK, takedown.StatusCode);

        var stored = await host.LoadJobAsync(jobId);
        Assert.Equal(JobStatus.Deleted, stored!.Status);
        Assert.Equal("Misleading salary", stored.ModerationReason);
        Assert.Equal(AdminId, stored.ModeratedBy);
        Assert.NotNull(stored.ModeratedAt);

        // Gone from the public read and from the public company page.
        using (var hidden = await host.SendAsync(HttpMethod.Get, $"/api/jobs/{jobId}", null, null))
            Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        using (var list = await host.SendAsync(HttpMethod.Get, $"/api/companies/{companyId}/jobs", null, null))
        {
            var jobs = await list.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(0, jobs.GetProperty("total").GetInt32());
        }

        // Admin detail still renders it with the audit.
        using (var detail = await host.SendAsync(HttpMethod.Get, $"/api/admin/jobs/{jobId}", AdminId, "Admin"))
        {
            var json = await detail.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("Deleted", json.GetProperty("status").GetString());
            Assert.Equal("Misleading salary", json.GetProperty("moderationReason").GetString());
        }

        // Restore re-activates, clears the audit and queues a job.updated re-index.
        using (var restore = await host.SendAsync(
                   HttpMethod.Post, $"/api/admin/jobs/{jobId}/restore", AdminId, "Admin"))
            Assert.Equal(HttpStatusCode.OK, restore.StatusCode);

        var restored = await host.LoadJobAsync(jobId);
        Assert.Equal(JobStatus.Active, restored!.Status);
        Assert.Null(restored.ModerationReason);
        Assert.Null(restored.ModeratedBy);
        Assert.Null(restored.ModeratedAt);

        using (var visible = await host.SendAsync(HttpMethod.Get, $"/api/jobs/{jobId}", null, null))
            Assert.Equal(HttpStatusCode.OK, visible.StatusCode);

        var outbox = await host.LoadOutboxAsync();
        var updated = Assert.Single(outbox, e => e.EventType == SharedKernel.Events.JobEventTypes.Updated);
        Assert.Contains("\"status\":\"Active\"", updated.Payload);

        using var listAfterRestore = await host.SendAsync(
            HttpMethod.Get, $"/api/companies/{companyId}/jobs", null, null);
        var jobsAfterRestore = await listAfterRestore.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, jobsAfterRestore.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Takedown_WithoutReason_Returns422AndLeavesTheJobActive()
    {
        await using var host = await JobApiTestHost.StartAsync();
        var companyId = await host.CreateCompanyAsync(OwnerRecruiter, "Reason Corp");
        var jobId = await CreateJobAsync(host, companyId);

        using var response = await host.SendAsync(
            HttpMethod.Post, $"/api/admin/jobs/{jobId}/takedown", AdminId, "Admin", new { reason = "  " });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(JobStatus.Active, (await host.LoadJobAsync(jobId))!.Status);
    }

    [Fact]
    public async Task Takedown_ByNonAdmin_Returns403()
    {
        await using var host = await JobApiTestHost.StartAsync();
        var companyId = await host.CreateCompanyAsync(OwnerRecruiter, "Guard Corp");
        var jobId = await CreateJobAsync(host, companyId);

        using var response = await host.SendAsync(
            HttpMethod.Post, $"/api/admin/jobs/{jobId}/takedown", OwnerRecruiter, "Recruiter",
            new { reason = "Spam" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(JobStatus.Active, (await host.LoadJobAsync(jobId))!.Status);
    }

    [Fact]
    public async Task CompanyJobs_ReturnsOnlyActiveJobsNewestFirst()
    {
        await using var host = await JobApiTestHost.StartAsync();
        var companyId = await host.CreateCompanyAsync(OwnerRecruiter, "Page Corp");
        var first = await CreateJobAsync(host, companyId, "First Job");
        var second = await CreateJobAsync(host, companyId, "Second Job");
        var closed = await host.SeedJobAsync(companyId, OwnerRecruiter, JobStatus.Closed);
        var deleted = await host.SeedJobAsync(companyId, OwnerRecruiter, JobStatus.Deleted);

        using var response = await host.SendAsync(
            HttpMethod.Get, $"/api/companies/{companyId}/jobs?page=0&size=10", null, null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, json.GetProperty("total").GetInt32());
        Assert.Equal(0, json.GetProperty("page").GetInt32());
        var ids = json.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList();
        Assert.DoesNotContain(closed, ids);
        Assert.DoesNotContain(deleted, ids);
        Assert.Contains(first, ids);
        Assert.Contains(second, ids);
        // Minimal public fields: no description, recruiter id or status.
        var item = json.GetProperty("items")[0];
        Assert.False(item.TryGetProperty("description", out _));
        Assert.False(item.TryGetProperty("recruiterId", out _));
        Assert.False(item.TryGetProperty("status", out _));
    }

    [Fact]
    public async Task CompanyJobs_UnknownCompany_Returns404()
    {
        await using var host = await JobApiTestHost.StartAsync();

        using var response = await host.SendAsync(
            HttpMethod.Get, $"/api/companies/{Guid.NewGuid()}/jobs", null, null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CompaniesMine_OwnerSeesIsCompanyAdminTrue_MemberSeesFalse()
    {
        await using var host = await JobApiTestHost.StartAsync();
        var companyId = await host.CreateCompanyAsync(OwnerRecruiter, "Role Corp");

        using var owner = await host.SendAsync(
            HttpMethod.Get, "/api/companies/mine", OwnerRecruiter, "Recruiter", companyId: companyId);
        Assert.Equal(HttpStatusCode.OK, owner.StatusCode);
        var ownerJson = await owner.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(companyId, ownerJson.GetProperty("id").GetGuid());
        Assert.True(ownerJson.GetProperty("isCompanyAdmin").GetBoolean());
        Assert.False(ownerJson.TryGetProperty("createdBy", out _));

        using var member = await host.SendAsync(
            HttpMethod.Get, "/api/companies/mine", MemberRecruiter, "Recruiter", companyId: companyId);
        Assert.Equal(HttpStatusCode.OK, member.StatusCode);
        var memberJson = await member.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(memberJson.GetProperty("isCompanyAdmin").GetBoolean());
    }

    [Fact]
    public async Task CompaniesMine_NoCompany_Returns404_AndAnonymousReturns401()
    {
        await using var host = await JobApiTestHost.StartAsync();

        using var none = await host.SendAsync(
            HttpMethod.Get, "/api/companies/mine", Guid.NewGuid(), "Recruiter");
        Assert.Equal(HttpStatusCode.NotFound, none.StatusCode);

        using var anonymous = await host.SendAsync(HttpMethod.Get, "/api/companies/mine", null, null);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task UpdateCompany_OwnerSucceeds_MemberAndAdminAreForbidden()
    {
        await using var host = await JobApiTestHost.StartAsync();
        var companyId = await host.CreateCompanyAsync(OwnerRecruiter, "Update Corp");
        var body = new
        {
            name = "Update Corp Renamed",
            taxCode = (string?)null,
            logoUrl = (string?)null,
            website = (string?)null,
            description = (string?)null,
            address = (string?)null,
            industry = (string?)null,
            size = (string?)null,
        };

        using var member = await host.SendAsync(
            HttpMethod.Put, $"/api/companies/{companyId}", MemberRecruiter, "Recruiter", body, companyId);
        Assert.Equal(HttpStatusCode.Forbidden, member.StatusCode);

        using var admin = await host.SendAsync(
            HttpMethod.Put, $"/api/companies/{companyId}", AdminId, "Admin", body);
        Assert.Equal(HttpStatusCode.Forbidden, admin.StatusCode);

        using var owner = await host.SendAsync(
            HttpMethod.Put, $"/api/companies/{companyId}", OwnerRecruiter, "Recruiter", body, companyId);
        Assert.Equal(HttpStatusCode.OK, owner.StatusCode);
    }

    private static async Task<Guid> CreateJobAsync(
        JobApiTestHost host, Guid companyId, string title = "Backend Developer")
    {
        using var response = await host.SendAsync(
            HttpMethod.Post, "/api/jobs", OwnerRecruiter, "Recruiter", JobBody(companyId, title));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }
}
