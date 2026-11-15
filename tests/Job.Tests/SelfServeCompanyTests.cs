using System.Security.Claims;
using Job.Api.Endpoints;
using Job.Core.Companies;
using Job.Core.Entities;
using Job.Core.InviteCodes;
using Job.Infrastructure.Clients;
using Job.Infrastructure.Companies;
using Job.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Job.Tests;

/// <summary>SRS AUTH-01-06, AUTH-01-10 to AUTH-01-15: work-email domain rules and join-request state machine.</summary>
public sealed class SelfServeDomainAndStateTests
{
    [Theory]
    [InlineData(" Acme.COM ", "acme.com")]
    [InlineData("sub.acme.vn.", "sub.acme.vn")]
    public void Normalize_TrimsLowerCasesAndAcceptsDomains(string raw, string expected) =>
        Assert.Equal(expected, WorkEmailDomain.Normalize(raw));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("localhost")]
    [InlineData("bad domain.com")]
    [InlineData("-bad.com")]
    [InlineData("acme..com")]
    public void Normalize_RejectsImplausibleDomains(string? raw) =>
        Assert.Null(WorkEmailDomain.Normalize(raw));

    [Fact]
    public void FromEmail_ReturnsNormalisedDomainPart()
    {
        Assert.Equal("acme.com", WorkEmailDomain.FromEmail("Ann@ACME.com"));
        Assert.Null(WorkEmailDomain.FromEmail("no-at-sign"));
    }

    [Theory]
    [InlineData("gmail.com")]
    [InlineData("proton.me")]
    public void IsBlocked_DefaultFreeProvidersAreBlocked(string domain) =>
        Assert.True(WorkEmailDomain.IsBlocked(domain));

    [Fact]
    public void IsBlocked_ConfiguredExtrasAreBlockedToo()
    {
        Assert.False(WorkEmailDomain.IsBlocked("acme.com"));
        Assert.True(WorkEmailDomain.IsBlocked("acme.com", ["acme.com"]));
    }

    [Fact]
    public void WaitingPeriod_DefaultsToSevenDaysAndHonoursConfig()
    {
        Assert.Equal(TimeSpan.FromDays(7), CompanySignUpSettings.ReadJoinRequestWaitingPeriod(new ConfigurationBuilder().Build()));
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [CompanySignUpSettings.JoinRequestTtlDaysKey] = "3" })
            .Build();
        Assert.Equal(TimeSpan.FromDays(3), CompanySignUpSettings.ReadJoinRequestWaitingPeriod(config));
    }

    [Fact]
    public void Company_StoresLowerCaseEmailDomainAndTransfersAdmin()
    {
        var founder = Guid.NewGuid();
        var company = new Company("Acme", founder, emailDomain: "Acme.com", verified: true);
        Assert.Equal("acme.com", company.EmailDomain);
        Assert.True(company.Verified);

        var successor = Guid.NewGuid();
        company.TransferAdmin(successor);
        Assert.Equal(successor, company.CreatedBy);
        Assert.Throws<ArgumentException>(() => company.TransferAdmin(Guid.Empty));
    }

    [Fact]
    public void JoinRequest_IsPendingUntilExpiry_ThenReadsAsExpired()
    {
        var created = DateTimeOffset.UtcNow.AddDays(-8);
        var request = new CompanyJoinRequest(Guid.NewGuid(), Guid.NewGuid(), created, TimeSpan.FromDays(7));

        Assert.Equal(CompanyJoinRequestStatus.Expired, request.EffectiveStatus(DateTimeOffset.UtcNow));
        Assert.Equal(CompanyJoinRequestStatus.Pending, request.EffectiveStatus(created.AddDays(1)));
    }

    [Fact]
    public void JoinRequest_ExpiredPendingCannotBeApproved()
    {
        var request = Expired();
        Assert.Throws<InvalidOperationException>(() => request.Approve(Guid.NewGuid(), DateTimeOffset.UtcNow));
    }

    [Fact]
    public void JoinRequest_RejectionIsFinal_ButEscalatesOnceAfterExpiry()
    {
        var request = Fresh();
        request.Reject(Guid.NewGuid(), DateTimeOffset.UtcNow, "not our team");
        Assert.Equal(CompanyJoinRequestStatus.Rejected, request.Status);
        Assert.Throws<InvalidOperationException>(() => request.Approve(Guid.NewGuid(), DateTimeOffset.UtcNow));

        // Before the waiting period a rejected request cannot be escalated.
        Assert.Throws<InvalidOperationException>(() => request.Escalate(DateTimeOffset.UtcNow));

        // After the waiting period it reads as Expired and escalates exactly once.
        var later = DateTimeOffset.UtcNow.AddDays(8);
        Assert.Equal(CompanyJoinRequestStatus.Expired, request.EffectiveStatus(later));
        request.Escalate(later);
        Assert.Equal(CompanyJoinRequestStatus.Escalated, request.Status);
        Assert.Throws<InvalidOperationException>(() => request.Escalate(later));
    }

    [Fact]
    public void JoinRequest_ApprovedRequestIsTerminalEvenAfterExpiry()
    {
        var request = Fresh();
        request.Approve(Guid.NewGuid(), DateTimeOffset.UtcNow);

        var later = DateTimeOffset.UtcNow.AddDays(30);
        Assert.Equal(CompanyJoinRequestStatus.Approved, request.EffectiveStatus(later));
        Assert.Throws<InvalidOperationException>(() => request.Escalate(later));
    }

    [Fact]
    public void JoinRequest_OnlyEscalatedRequestsReachSupport()
    {
        var request = Expired();
        Assert.Throws<InvalidOperationException>(() => request.DecideBySupport(true, null, DateTimeOffset.UtcNow));

        request.Escalate(DateTimeOffset.UtcNow);
        request.DecideBySupport(approve: true, reason: "  verified by phone  ", now: DateTimeOffset.UtcNow);
        Assert.Equal(CompanyJoinRequestStatus.SupportApproved, request.Status);
        Assert.Equal("verified by phone", request.DecisionReason);
    }

    [Fact]
    public void JoinRequest_MarkExpiredOnlyAppliesToPending()
    {
        var pending = Expired();
        pending.MarkExpired();
        Assert.Equal(CompanyJoinRequestStatus.Expired, pending.Status);

        var rejected = Fresh();
        rejected.Reject(Guid.NewGuid(), DateTimeOffset.UtcNow, null);
        Assert.Throws<InvalidOperationException>(() => rejected.MarkExpired());
    }

    private static CompanyJoinRequest Fresh() =>
        new(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, TimeSpan.FromDays(7));

    private static CompanyJoinRequest Expired() =>
        new(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(-8), TimeSpan.FromDays(7));
}

/// <summary>Internal and public company endpoints on the in-memory provider (route wiring is in the integration tests).</summary>
public sealed class SelfServeCompanyEndpointTests : IDisposable
{
    private const string InternalToken = "test-auth-caller-token";
    private static readonly Guid Admin = Guid.NewGuid();
    private static readonly Guid Requester = Guid.NewGuid();

    private readonly JobDbContext _db;
    private readonly IConfiguration _config;
    private readonly Guid _companyId;

    public SelfServeCompanyEndpointTests()
    {
        _db = new JobDbContext(new DbContextOptionsBuilder<JobDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [InternalStatsEndpoints.AuthCallerTokenKey] = InternalToken,
            })
            .Build();

        var company = new Company("Acme Corp", Admin, emailDomain: "acme.com", verified: true);
        _db.Companies.Add(company);
        _db.SaveChanges();
        _companyId = company.Id;
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task PostCompanies_AlwaysConflictsForARecruiter()
    {
        var result = CompanyEndpoints.CreateCompany(RecruiterContext(Requester));
        var body = await Json(result);
        Assert.Equal(409, ((IStatusCodeHttpResult)result).StatusCode);
        Assert.Equal("ACCOUNT_ALREADY_HAS_COMPANY", body.GetProperty("errorCode").GetString());
        Assert.Equal(1, await _db.Companies.CountAsync());
    }

    [Fact]
    public void PostCompanies_NonRecruiterIsForbidden()
    {
        var result = CompanyEndpoints.CreateCompany(RecruiterContext(Requester, "User"));
        Assert.Equal(403, ((IStatusCodeHttpResult)result).StatusCode);
    }

    [Fact]
    public async Task Availability_ReportsNameAndTaxCodeConflictsCaseInsensitively()
    {
        await _db.Companies.AddAsync(new Company("Beta Ltd", Guid.NewGuid(), taxCode: "0101234567"));
        await _db.SaveChangesAsync();

        var free = await Availability("Gamma Co", null);
        var takenName = await Availability("  acme CORP ", null);
        var takenTax = await Availability("Gamma Co", " 0101234567 ");

        Assert.True(Available(free));
        Assert.False(Available(takenName));
        Assert.False(Available(takenTax));
    }

    [Fact]
    public async Task Availability_IsFailClosedWithoutToken()
    {
        var result = await InternalCompanyEndpoints.CheckAvailabilityAsync(
            new InternalCompanyEndpoints.AvailabilityRequest("Gamma Co", null),
            AuthContext("Bearer wrong"), _db, _config, CancellationToken.None);
        Assert.Equal(401, ((IStatusCodeHttpResult)result).StatusCode);
    }

    [Fact]
    public async Task DomainMatch_ReturnsOnlyVerifiedCompaniesAndNeverBlocklistedDomains()
    {
        await _db.Companies.AddAsync(new Company("Unverified Co", Guid.NewGuid(), emailDomain: "unverified.com"));
        await _db.SaveChangesAsync();

        Assert.Equal((Guid?)_companyId, await DomainMatch("ACME.com"));
        Assert.Null(await DomainMatch("unverified.com"));
        Assert.Null(await DomainMatch("gmail.com"));
        Assert.Null(await DomainMatch("not a domain"));
    }

    [Fact]
    public async Task CreateCompanyForFounder_CreatesVerifiedCompanyWithDomain()
    {
        var founder = Guid.NewGuid();
        var result = await CreateForFounder("Delta JSC", "0312345678", founder, "delta.vn");

        var created = Assert.IsType<Created<InternalCompanyEndpoints.CreateCompanyResponse>>(result);
        var stored = await _db.Companies.SingleAsync(c => c.CreatedBy == founder);
        Assert.Equal(created.Value!.Id, stored.Id);
        Assert.True(stored.Verified);
        Assert.Equal("delta.vn", stored.EmailDomain);
    }

    [Fact]
    public async Task CreateCompanyForFounder_IsIdempotentForTheSameFounderAndDetails()
    {
        var founder = Guid.NewGuid();
        await CreateForFounder("Delta JSC", "0312345678", founder, "delta.vn");
        var retry = await CreateForFounder("Delta JSC", "0312345678", founder, "delta.vn");

        Assert.Equal(200, ((IStatusCodeHttpResult)retry).StatusCode);
        Assert.Equal(2, await _db.Companies.CountAsync());
    }

    [Fact]
    public async Task CreateCompanyForFounder_ConflictsAreStableCodes()
    {
        var byName = await CreateForFounder("ACME CORP", null, Guid.NewGuid(), "fresh.vn");
        var byDomain = await CreateForFounder("Fresh Name", null, Guid.NewGuid(), "acme.com");
        await _db.Companies.AddAsync(new Company("Tax Holder", Guid.NewGuid(), taxCode: "9999"));
        await _db.SaveChangesAsync();
        var byTax = await CreateForFounder("Another", "9999", Guid.NewGuid(), "other.vn");

        Assert.Equal("COMPANY_NAME_TAKEN", ErrorCode(byName));
        Assert.Equal("COMPANY_DOMAIN_TAKEN", ErrorCode(byDomain));
        Assert.Equal("COMPANY_TAX_CODE_TAKEN", ErrorCode(byTax));
        Assert.All(new[] { byName, byDomain, byTax }, r => Assert.Equal(409, ((IStatusCodeHttpResult)r).StatusCode));
    }

    [Fact]
    public async Task CreateCompanyForFounder_RefusesBlocklistedDomainsAndSecondCompanies()
    {
        var blocked = await CreateForFounder("Free Co", null, Guid.NewGuid(), "gmail.com");
        Assert.Equal(422, ((IStatusCodeHttpResult)blocked).StatusCode);
        Assert.Equal("WORK_EMAIL_REQUIRED", ErrorCode(blocked));

        var second = await CreateForFounder("Second Co", null, Admin, "second.vn");
        Assert.Equal("ACCOUNT_ALREADY_HAS_COMPANY", ErrorCode(second));
    }

    [Fact]
    public async Task JoinRequest_IsCreatedOncePerAccountAndReplayedForTheSameCompany()
    {
        var first = await CreateJoinRequest(Requester, _companyId);
        var replay = await CreateJoinRequest(Requester, _companyId);
        await _db.Companies.AddAsync(new Company("Second Verified", Guid.NewGuid(), emailDomain: "second.vn", verified: true));
        await _db.SaveChangesAsync();
        var secondCompany = await _db.Companies.Where(c => c.Name == "Second Verified").Select(c => c.Id).SingleAsync();
        var other = await CreateJoinRequest(Requester, secondCompany);

        Assert.IsType<Created<InternalCompanyEndpoints.JoinRequestResponse>>(first);
        Assert.Equal(200, ((IStatusCodeHttpResult)replay).StatusCode);
        Assert.Equal("JOIN_REQUEST_EXISTS", ErrorCode(other));
        Assert.Equal(1, await _db.CompanyJoinRequests.CountAsync());
    }

    [Fact]
    public async Task JoinRequest_RequiresAVerifiedCompany()
    {
        await _db.Companies.AddAsync(new Company("Unverified Co", Guid.NewGuid(), emailDomain: "u.vn"));
        await _db.SaveChangesAsync();
        var unverified = await _db.Companies.Where(c => c.Name == "Unverified Co").Select(c => c.Id).SingleAsync();

        var result = await CreateJoinRequest(Requester, unverified);
        Assert.Equal("COMPANY_NOT_VERIFIED", ErrorCode(result));
    }

    [Fact]
    public async Task AdminListsAndApprovesOnlyWithTheCompanyAdminRole()
    {
        var request = await SeedRequest();
        var linker = new FakeLinker(LinkCompanyResult.Linked);

        var forbidden = await CompanyJoinRequestEndpoints.ApproveJoinRequest(
            _companyId, request.Id, RecruiterContext(Guid.NewGuid()), _db, linker, CancellationToken.None);
        var notRecruiter = await CompanyJoinRequestEndpoints.ApproveJoinRequest(
            _companyId, request.Id, RecruiterContext(Admin, "User"), _db, linker, CancellationToken.None);
        var listForbidden = await CompanyJoinRequestEndpoints.ListJoinRequests(
            _companyId, RecruiterContext(Guid.NewGuid()), _db, 1, 20, CancellationToken.None);

        Assert.Equal("JOIN_REQUEST_NOT_ADMIN", ErrorCode(forbidden));
        Assert.Equal("JOIN_REQUEST_NOT_ADMIN", ErrorCode(notRecruiter));
        Assert.Equal("JOIN_REQUEST_NOT_ADMIN", ErrorCode(listForbidden));
        Assert.Equal(0, linker.Calls);
        Assert.Equal(CompanyJoinRequestStatus.Pending, (await _db.CompanyJoinRequests.SingleAsync()).Status);
    }

    [Fact]
    public async Task Approve_StaysPendingWhenAuthIsUnavailable_AndSucceedsOnRetry()
    {
        var request = await SeedRequest();

        var down = await CompanyJoinRequestEndpoints.ApproveJoinRequest(
            _companyId, request.Id, RecruiterContext(Admin), _db, new FakeLinker(LinkCompanyResult.Unavailable), CancellationToken.None);
        Assert.Equal(503, ((IStatusCodeHttpResult)down).StatusCode);
        Assert.Equal(CompanyJoinRequestStatus.Pending, (await _db.CompanyJoinRequests.SingleAsync()).Status);

        var up = await CompanyJoinRequestEndpoints.ApproveJoinRequest(
            _companyId, request.Id, RecruiterContext(Admin), _db, new FakeLinker(LinkCompanyResult.Linked), CancellationToken.None);
        Assert.Equal(200, ((IStatusCodeHttpResult)up).StatusCode);
        Assert.Equal(CompanyJoinRequestStatus.Approved, (await _db.CompanyJoinRequests.SingleAsync()).Status);
    }

    [Fact]
    public async Task Reject_IsFinalAndExpiredRequestsCannotBeDecided()
    {
        var request = await SeedRequest();
        var rejected = await CompanyJoinRequestEndpoints.RejectJoinRequest(
            _companyId, request.Id, new CompanyJoinRequestEndpoints.RejectJoinRequestBody("duplicate"),
            RecruiterContext(Admin), _db, CancellationToken.None);
        Assert.Equal(200, ((IStatusCodeHttpResult)rejected).StatusCode);

        var again = await CompanyJoinRequestEndpoints.ApproveJoinRequest(
            _companyId, request.Id, RecruiterContext(Admin), _db, new FakeLinker(LinkCompanyResult.Linked), CancellationToken.None);
        Assert.Equal("JOIN_REQUEST_STATE_INVALID", ErrorCode(again));
    }

    [Fact]
    public async Task Requester_SeesStatusAndEscalatesOnlyAfterExpiryAndOnlyOnce()
    {
        await _db.CompanyJoinRequests.AddAsync(new CompanyJoinRequest(
            _companyId, Requester, DateTimeOffset.UtcNow.AddDays(-10), TimeSpan.FromDays(7)));
        await _db.SaveChangesAsync();

        var status = await CompanyJoinRequestEndpoints.GetMyJoinRequest(RecruiterContext(Requester, "User"), _db, CancellationToken.None);
        var body = await Json(status);
        Assert.Equal("Expired", body.GetProperty("status").GetString());
        Assert.True(body.GetProperty("canEscalate").GetBoolean());

        var first = await CompanyJoinRequestEndpoints.EscalateMyJoinRequest(RecruiterContext(Requester, "User"), _db, CancellationToken.None);
        var second = await CompanyJoinRequestEndpoints.EscalateMyJoinRequest(RecruiterContext(Requester, "User"), _db, CancellationToken.None);
        Assert.Equal(200, ((IStatusCodeHttpResult)first).StatusCode);
        Assert.Equal("JOIN_REQUEST_STATE_INVALID", ErrorCode(second));
        Assert.Equal(CompanyJoinRequestStatus.Escalated, (await _db.CompanyJoinRequests.SingleAsync()).Status);
    }

    [Fact]
    public async Task Requester_CannotEscalateBeforeExpiry()
    {
        await SeedRequest();
        var result = await CompanyJoinRequestEndpoints.EscalateMyJoinRequest(RecruiterContext(Requester, "User"), _db, CancellationToken.None);
        Assert.Equal("JOIN_REQUEST_STATE_INVALID", ErrorCode(result));
    }

    [Fact]
    public async Task SupportApprove_LinksAdminChangesAdminAndRevokesOnlyPreviousAdminsUnusedCodes()
    {
        var request = await SeedEscalatedRequest();
        var issuedByOldAdmin = new InviteCode(_companyId, InviteCodeFormat.Hash("AAAAAAAAAAAA"), "a@acme.com", Admin, DateTimeOffset.UtcNow, TimeSpan.FromDays(7));
        var usedByOldAdmin = new InviteCode(_companyId, InviteCodeFormat.Hash("BBBBBBBBBBBB"), "b@acme.com", Admin, DateTimeOffset.UtcNow, TimeSpan.FromDays(7));
        usedByOldAdmin.MarkUsed(DateTimeOffset.UtcNow);
        await _db.InviteCodes.AddRangeAsync(issuedByOldAdmin, usedByOldAdmin);
        await _db.SaveChangesAsync();

        var result = await InternalCompanyEndpoints.SupportDecisionAsync(
            request.Id,
            new InternalCompanyEndpoints.SupportDecisionRequest("approve", "checked by phone", "support.agent"),
            AuthContext(), _db, _config, new FakeLinker(LinkCompanyResult.Linked), NullLoggerFactory.Instance, CancellationToken.None);

        Assert.Equal(200, ((IStatusCodeHttpResult)result).StatusCode);
        var company = await _db.Companies.SingleAsync(c => c.Id == _companyId);
        Assert.Equal(Requester, company.CreatedBy);
        Assert.Equal(CompanyJoinRequestStatus.SupportApproved, (await _db.CompanyJoinRequests.SingleAsync()).Status);
        Assert.Equal(InviteCodeStatus.Revoked, (await _db.InviteCodes.SingleAsync(c => c.Email == "a@acme.com")).Status);
        // A code already used by the previous admin is not changed (AUTH-01-09).
        Assert.Equal(InviteCodeStatus.Active, (await _db.InviteCodes.SingleAsync(c => c.Email == "b@acme.com")).Status);
    }

    [Fact]
    public async Task SupportApprove_WhenAuthFailsStaysEscalatedAndAdminDoesNotChange()
    {
        var request = await SeedEscalatedRequest();

        var result = await InternalCompanyEndpoints.SupportDecisionAsync(
            request.Id,
            new InternalCompanyEndpoints.SupportDecisionRequest("approve", null, "support.agent"),
            AuthContext(), _db, _config, new FakeLinker(LinkCompanyResult.Unavailable), NullLoggerFactory.Instance, CancellationToken.None);

        Assert.Equal(503, ((IStatusCodeHttpResult)result).StatusCode);
        Assert.Equal(CompanyJoinRequestStatus.Escalated, (await _db.CompanyJoinRequests.SingleAsync()).Status);
        Assert.Equal(Admin, (await _db.Companies.SingleAsync(c => c.Id == _companyId)).CreatedBy);
    }

    [Fact]
    public async Task SupportDecision_ValidatesInputAndOnlyDecidesEscalatedRequests()
    {
        var pending = await SeedRequest();
        var badDecision = await InternalCompanyEndpoints.SupportDecisionAsync(
            pending.Id, new InternalCompanyEndpoints.SupportDecisionRequest("maybe", null, "op"),
            AuthContext(), _db, _config, new FakeLinker(LinkCompanyResult.Linked), NullLoggerFactory.Instance, CancellationToken.None);
        var noOperator = await InternalCompanyEndpoints.SupportDecisionAsync(
            pending.Id, new InternalCompanyEndpoints.SupportDecisionRequest("reject", null, " "),
            AuthContext(), _db, _config, new FakeLinker(LinkCompanyResult.Linked), NullLoggerFactory.Instance, CancellationToken.None);
        var notEscalated = await InternalCompanyEndpoints.SupportDecisionAsync(
            pending.Id, new InternalCompanyEndpoints.SupportDecisionRequest("reject", null, "op"),
            AuthContext(), _db, _config, new FakeLinker(LinkCompanyResult.Linked), NullLoggerFactory.Instance, CancellationToken.None);

        Assert.Equal(422, ((IStatusCodeHttpResult)badDecision).StatusCode);
        Assert.Equal(422, ((IStatusCodeHttpResult)noOperator).StatusCode);
        Assert.Equal("JOIN_REQUEST_STATE_INVALID", ErrorCode(notEscalated));
    }

    [Fact]
    public async Task InternalCompanyEndpoints_FailClosedWithoutToken()
    {
        var result = await InternalCompanyEndpoints.DomainMatchAsync(
            new InternalCompanyEndpoints.DomainMatchRequest("acme.com"),
            AuthContext(), _db, new ConfigurationBuilder().Build(), CancellationToken.None);
        Assert.Equal(401, ((IStatusCodeHttpResult)result).StatusCode);
    }

    private async Task<CompanyJoinRequest> SeedRequest()
    {
        var request = new CompanyJoinRequest(_companyId, Requester, DateTimeOffset.UtcNow, TimeSpan.FromDays(7));
        await _db.CompanyJoinRequests.AddAsync(request);
        await _db.SaveChangesAsync();
        return request;
    }

    private async Task<CompanyJoinRequest> SeedEscalatedRequest()
    {
        var request = new CompanyJoinRequest(_companyId, Requester, DateTimeOffset.UtcNow.AddDays(-10), TimeSpan.FromDays(7));
        request.Escalate(DateTimeOffset.UtcNow);
        await _db.CompanyJoinRequests.AddAsync(request);
        await _db.SaveChangesAsync();
        return request;
    }

    private Task<IResult> CreateForFounder(string name, string? taxCode, Guid founder, string domain) =>
        InternalCompanyEndpoints.CreateCompanyForFounderAsync(
            new InternalCompanyEndpoints.CreateCompanyRequest(name, taxCode, founder, domain),
            AuthContext(), _db, _config, NullLoggerFactory.Instance, CancellationToken.None);

    private Task<IResult> CreateJoinRequest(Guid userId, Guid companyId) =>
        InternalCompanyEndpoints.CreateJoinRequestAsync(
            new InternalCompanyEndpoints.CreateJoinRequestRequest(userId, companyId),
            AuthContext(), _db, _config, CancellationToken.None);

    private async Task<IResult> Availability(string name, string? taxCode) =>
        await InternalCompanyEndpoints.CheckAvailabilityAsync(
            new InternalCompanyEndpoints.AvailabilityRequest(name, taxCode),
            AuthContext(), _db, _config, CancellationToken.None);

    private static bool Available(IResult result) =>
        Assert.IsType<Ok<InternalCompanyEndpoints.AvailabilityResponse>>(result).Value!.Available;

    private async Task<Guid?> DomainMatch(string domain)
    {
        var result = await InternalCompanyEndpoints.DomainMatchAsync(
            new InternalCompanyEndpoints.DomainMatchRequest(domain), AuthContext(), _db, _config, CancellationToken.None);
        return Assert.IsType<Ok<InternalCompanyEndpoints.DomainMatchResponse>>(result).Value!.CompanyId;
    }

    private static readonly IServiceProvider ResultServices =
        new ServiceCollection().AddOptions().AddLogging().BuildServiceProvider();

    /// <summary>Runs an IResult against a bare HttpContext and returns the JSON body.</summary>
    private static async Task<System.Text.Json.JsonElement> Json(IResult result)
    {
        var ctx = new DefaultHttpContext { RequestServices = ResultServices };
        ctx.Response.Body = new MemoryStream();
        await result.ExecuteAsync(ctx);
        ctx.Response.Body.Position = 0;
        return await System.Text.Json.JsonSerializer.DeserializeAsync<System.Text.Json.JsonElement>(ctx.Response.Body);
    }

    private static string ErrorCode(IResult result) =>
        Json(result).GetAwaiter().GetResult().GetProperty("errorCode").GetString()!;

    private static HttpContext RecruiterContext(Guid userId, string role = "Recruiter")
    {
        var ctx = new DefaultHttpContext();
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(
            new[]
            {
                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                new Claim(ClaimTypes.Role, role),
            }, "test"));
        return ctx;
    }

    private static HttpContext AuthContext(string? authorization = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers.Authorization = authorization ?? $"Bearer {InternalToken}";
        return ctx;
    }

    private sealed class FakeLinker(LinkCompanyResult result) : IAuthCompanyLinker
    {
        public int Calls { get; private set; }

        public Task<LinkCompanyResult> LinkCompanyAsync(Guid userId, Guid companyId, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }
}
