using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Job.Infrastructure.Clients;
using Microsoft.Extensions.DependencyInjection;

namespace Job.Tests.Integration;

/// <summary>Route, auth and status wiring for the self-serve company endpoints over HTTP (TestServer).</summary>
public sealed class SelfServeHttpTests
{
    private static readonly Guid Founder = Guid.NewGuid();
    private static readonly Guid Invitee = Guid.NewGuid();

    [Fact]
    public async Task PostCompanies_ForRecruiter_Returns409WithStableCode()
    {
        await using var host = await JobApiTestHost.StartAsync();
        using var response = await host.SendAsync(HttpMethod.Post, "/api/companies", Founder, "Recruiter", new { name = "X" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ACCOUNT_ALREADY_HAS_COMPANY", json.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task InternalCompanyRoutes_FailClosedWithoutTheAuthToken()
    {
        await using var host = await JobApiTestHost.StartAsync();
        var routes = new[]
        {
            "/api/internal/companies/availability",
            "/api/internal/companies/domain-match",
            "/api/internal/companies",
            "/api/internal/company-join-requests",
            $"/api/internal/company-join-requests/{Guid.NewGuid()}/support-decision",
            "/api/internal/invite-codes/check",
            "/api/internal/invite-codes/redeem",
        };

        foreach (var route in routes)
        {
            using var none = await host.Client.PostAsJsonAsync(route, new { });
            using var wrong = await Send(host, route, "Bearer not-the-token");
            Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }
    }

    [Fact]
    public async Task Availability_OverHttpWithTheAuthToken()
    {
        await using var host = await JobApiTestHost.StartAsync();
        await host.CreateCompanyAsync(Founder, "Acme Corp");

        using var taken = await Send(host, "/api/internal/companies/availability",
            $"Bearer {JobApiTestHost.AuthInternalToken}", new { companyName = "acme corp", taxCode = (string?)null });
        using var free = await Send(host, "/api/internal/companies/availability",
            $"Bearer {JobApiTestHost.AuthInternalToken}", new { companyName = "Fresh Ltd", taxCode = (string?)null });

        Assert.Equal(HttpStatusCode.OK, taken.StatusCode);
        Assert.False((await taken.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("available").GetBoolean());
        Assert.True((await free.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("available").GetBoolean());
    }

    [Fact]
    public async Task CreateCompany_OverHttp_ReturnsCompanyIdAsTheContractName()
    {
        await using var host = await JobApiTestHost.StartAsync();
        var founder = Guid.NewGuid();

        using var response = await Send(host, "/api/internal/companies",
            $"Bearer {JobApiTestHost.AuthInternalToken}",
            new { name = "New Founder Co", taxCode = (string?)null, createdBy = founder, emailDomain = "founder-co.test" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        // auth-svc binds this property into CreatedCompanyBody(Guid CompanyId): the name is the contract.
        Assert.True(json.TryGetProperty("companyId", out var companyId));
        Assert.NotEqual(Guid.Empty, companyId.GetGuid());
        Assert.False(json.TryGetProperty("id", out _));
    }

    [Fact]
    public async Task InviteCode_IssuedOverHttp_RedeemsOnlyWithTheBoundEmail()
    {
        await using var host = await JobApiTestHost.StartAsync();
        var companyId = await host.CreateCompanyAsync(Founder, "Acme Corp");

        using var issue = await host.SendAsync(HttpMethod.Post, $"/api/companies/{companyId}/invite-codes",
            Founder, "Recruiter", new { email = "invitee@acme.com" });
        Assert.Equal(HttpStatusCode.Created, issue.StatusCode);
        var code = (await issue.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!;

        using var wrongEmail = await Send(host, "/api/internal/invite-codes/redeem",
            $"Bearer {JobApiTestHost.AuthInternalToken}", new { code, email = "other@acme.com" });
        using var redeem = await Send(host, "/api/internal/invite-codes/redeem",
            $"Bearer {JobApiTestHost.AuthInternalToken}", new { code, email = "invitee@acme.com" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, wrongEmail.StatusCode);
        Assert.Equal(HttpStatusCode.OK, redeem.StatusCode);
    }

    [Fact]
    public async Task JoinRequest_RequesterSeesStatus_AndApproveWithoutAuthIsUnavailable()
    {
        await using var host = await JobApiTestHost.StartAsync();
        var companyId = await host.CreateCompanyAsync(Founder, "Acme Corp");
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Job.Infrastructure.Data.JobDbContext>();
            var company = await db.Companies.FindAsync(companyId);
            company!.Verify();
            await db.SaveChangesAsync();
        }

        using var create = await Send(host, "/api/internal/company-join-requests",
            $"Bearer {JobApiTestHost.AuthInternalToken}", new { userId = Invitee, companyId });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        using var mine = await host.SendAsync(HttpMethod.Get, "/api/companies/me/join-request", Invitee, "User");
        Assert.Equal(HttpStatusCode.OK, mine.StatusCode);
        Assert.Equal("Pending", (await mine.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());

        var requestId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("joinRequestId").GetGuid();
        using var approve = await host.SendAsync(HttpMethod.Post,
            $"/api/companies/{companyId}/join-requests/{requestId}/approve", Founder, "Recruiter");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, approve.StatusCode);

        using var notAdmin = await host.SendAsync(HttpMethod.Get,
            $"/api/companies/{companyId}/join-requests", Invitee, "User");
        Assert.Equal(HttpStatusCode.Forbidden, notAdmin.StatusCode);
    }

    private static Task<HttpResponseMessage> Send(JobApiTestHost host, string route, string authorization, object? body = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, route)
        {
            Content = JsonContent.Create(body ?? new { }),
        };
        request.Headers.Authorization = AuthenticationHeaderValue.Parse(authorization);
        return host.Client.SendAsync(request);
    }
}
