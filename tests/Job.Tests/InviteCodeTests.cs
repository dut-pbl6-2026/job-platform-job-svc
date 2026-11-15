using System.Security.Claims;
using Job.Api.Endpoints;
using Job.Core.Entities;
using Job.Core.InviteCodes;
using Job.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Job.Tests;

/// <summary>SRS AUTH-01-09: code format, hashing, issue rules, internal check/redeem/release.</summary>
public sealed class InviteCodeFormatTests
{
    [Fact]
    public void Generate_ReturnsTwelveCharsFromAlphabetWithoutAmbiguousSymbols()
    {
        for (var i = 0; i < 200; i++)
        {
            var code = InviteCodeFormat.Generate();
            Assert.Equal(InviteCodeFormat.CodeLength, code.Length);
            Assert.All(code, ch => Assert.Contains(ch, InviteCodeFormat.Alphabet));
        }
        Assert.DoesNotContain('0', InviteCodeFormat.Alphabet);
        Assert.DoesNotContain('O', InviteCodeFormat.Alphabet);
        Assert.DoesNotContain('1', InviteCodeFormat.Alphabet);
        Assert.DoesNotContain('I', InviteCodeFormat.Alphabet);
    }

    [Fact]
    public void Display_GroupsInFoursWithDashes()
    {
        Assert.Equal("ABCD-EFGH-JKLM", InviteCodeFormat.Display("ABCDEFGHJKLM"));
    }

    [Theory]
    [InlineData("abcd-efgh-jklm", "ABCDEFGHJKLM")]
    [InlineData("  ABCD EFGH JKLM ", "ABCDEFGHJKLM")]
    [InlineData("ABCDEFGHJKLM", "ABCDEFGHJKLM")]
    public void Normalize_AcceptsCaseDashesAndSpaces(string raw, string expected)
    {
        Assert.Equal(expected, InviteCodeFormat.Normalize(raw));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ABCD-EFGH-JKL")]        // too short
    [InlineData("ABCD-EFGH-JKLMN")]      // too long
    [InlineData("ABCD-EFGH-JK0M")]       // ambiguous zero
    [InlineData("ABCD-EFGH-JKIM")]       // ambiguous I
    [InlineData("ABCD-EFGH-JK!M")]       // symbol outside alphabet
    public void Normalize_RejectsMalformedCodes(string? raw)
    {
        Assert.Null(InviteCodeFormat.Normalize(raw));
    }

    [Fact]
    public void Hash_IsSha256HexOfNormalisedCode()
    {
        var hash = InviteCodeFormat.Hash("ABCDEFGHJKLM");
        Assert.Equal(64, hash.Length);
        Assert.Matches("^[0-9a-f]{64}$", hash);
        Assert.Equal(hash, InviteCodeFormat.Hash("ABCDEFGHJKLM"));
    }
}

public sealed class InviteCodeEndpointTests : IDisposable
{
    private const string InternalToken = "test-auth-caller-token";
    private static readonly Guid Creator = Guid.NewGuid();
    private readonly JobDbContext _db;
    private readonly IConfiguration _config;
    private readonly Guid _companyId;

    public InviteCodeEndpointTests()
    {
        var options = new DbContextOptionsBuilder<JobDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new JobDbContext(options);
        _db.Database.EnsureCreated();
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [InternalStatsEndpoints.AuthCallerTokenKey] = InternalToken,
            })
            .Build();

        var company = new Company("Acme Corp", Creator);
        _db.Companies.Add(company);
        _db.SaveChanges();
        _companyId = company.Id;
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Issue_CreatorGetsPlainCodeOnceAndOnlyHashIsStored()
    {
        var result = await InviteCodeEndpoints.IssueInviteCodeAsync(
            _companyId, RecruiterContext(Creator), _db, _config);

        var created = Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.Created<InviteCodeEndpoints.IssuedInviteCodeResponse>>(result);
        var body = created.Value!;
        Assert.Matches("^[A-Z2-9]{4}-[A-Z2-9]{4}-[A-Z2-9]{4}$", body.Code);

        var stored = await _db.InviteCodes.SingleAsync();
        Assert.NotEqual(body.Code, stored.CodeHash);
        Assert.Equal(InviteCodeFormat.Hash(InviteCodeFormat.Normalize(body.Code)!), stored.CodeHash);
        Assert.Equal(_companyId, stored.CompanyId);
        Assert.Equal(Creator, stored.CreatedBy);
        Assert.Null(stored.UsedAt);
        var ttl = stored.ExpiresAt - DateTimeOffset.UtcNow;
        Assert.InRange(ttl.TotalDays, 6.99, 7.01);
    }

    [Fact]
    public async Task Issue_NonCreatorRecruiterIsForbidden()
    {
        var result = await InviteCodeEndpoints.IssueInviteCodeAsync(
            _companyId, RecruiterContext(Guid.NewGuid()), _db, _config);

        Assert.Equal(403, ((IStatusCodeHttpResult)result).StatusCode);
        Assert.Empty(_db.InviteCodes);
    }

    [Fact]
    public async Task Issue_LegacyEmptyCreatorIsRefused()
    {
        var legacy = new Company("Legacy Co", Guid.NewGuid());
        typeof(Company).GetProperty(nameof(Company.CreatedBy))!
            .SetValue(legacy, Guid.Empty);
        _db.Companies.Add(legacy);
        await _db.SaveChangesAsync();

        var result = await InviteCodeEndpoints.IssueInviteCodeAsync(
            legacy.Id, RecruiterContext(Guid.Empty), _db, _config);

        Assert.Equal(403, ((IStatusCodeHttpResult)result).StatusCode);
    }

    [Fact]
    public async Task Issue_NonRecruiterIsForbidden()
    {
        var result = await InviteCodeEndpoints.IssueInviteCodeAsync(
            _companyId, RecruiterContext(Creator, role: "User"), _db, _config);

        Assert.Equal(403, ((IStatusCodeHttpResult)result).StatusCode);
    }

    [Fact]
    public async Task Check_ValidCodeReturnsCompany_AndDoesNotConsume()
    {
        var code = await IssueAsync();

        var first = await InviteCodeEndpoints.CheckInviteCodeAsync(
            new InviteCodeEndpoints.InviteCodeRequest(code), AuthContext(), _db, _config);
        var second = await InviteCodeEndpoints.CheckInviteCodeAsync(
            new InviteCodeEndpoints.InviteCodeRequest(code), AuthContext(), _db, _config);

        Assert.Equal(200, ((IStatusCodeHttpResult)first).StatusCode);
        Assert.Equal(200, ((IStatusCodeHttpResult)second).StatusCode);
    }

    [Fact]
    public async Task Redeem_IsSingleUse_AndSecondAttemptGetsSameInvalidError()
    {
        var code = await IssueAsync();

        var first = await InviteCodeEndpoints.RedeemInviteCodeAsync(
            new InviteCodeEndpoints.InviteCodeRequest(code.ToLowerInvariant()), AuthContext(), _db, _config);
        var second = await InviteCodeEndpoints.RedeemInviteCodeAsync(
            new InviteCodeEndpoints.InviteCodeRequest(code), AuthContext(), _db, _config);

        Assert.Equal(200, ((IStatusCodeHttpResult)first).StatusCode);
        Assert.Equal(422, ((IStatusCodeHttpResult)second).StatusCode);
        Assert.NotNull((await _db.InviteCodes.SingleAsync()).UsedAt);
    }

    [Fact]
    public async Task Release_ClearsUsedSoTheCodeCanBeRedeemedAgain()
    {
        var code = await IssueAsync();
        var redeemed = await InviteCodeEndpoints.RedeemInviteCodeAsync(
            new InviteCodeEndpoints.InviteCodeRequest(code), AuthContext(), _db, _config);
        var inviteCodeId = Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.Ok<InviteCodeEndpoints.InviteCodeRedeemResponse>>(redeemed).Value!.InviteCodeId;

        var released = await InviteCodeEndpoints.ReleaseInviteCodeAsync(inviteCodeId, AuthContext(), _db, _config);
        var again = await InviteCodeEndpoints.RedeemInviteCodeAsync(
            new InviteCodeEndpoints.InviteCodeRequest(code), AuthContext(), _db, _config);

        Assert.Equal(204, ((IStatusCodeHttpResult)released).StatusCode);
        Assert.Equal(200, ((IStatusCodeHttpResult)again).StatusCode);
    }

    [Fact]
    public async Task Check_ExpiredCodeIsInvalid()
    {
        var code = await IssueAsync();
        var entity = await _db.InviteCodes.SingleAsync();
        typeof(InviteCode).GetProperty(nameof(InviteCode.ExpiresAt))!
            .SetValue(entity, DateTimeOffset.UtcNow.AddMinutes(-1));
        await _db.SaveChangesAsync();

        var result = await InviteCodeEndpoints.CheckInviteCodeAsync(
            new InviteCodeEndpoints.InviteCodeRequest(code), AuthContext(), _db, _config);

        Assert.Equal(422, ((IStatusCodeHttpResult)result).StatusCode);
    }

    [Fact]
    public async Task Check_UnknownAndMalformedCodesGiveTheSameError()
    {
        await IssueAsync();

        var unknown = await InviteCodeEndpoints.CheckInviteCodeAsync(
            new InviteCodeEndpoints.InviteCodeRequest("ZZZZ-ZZZZ-ZZZZ"), AuthContext(), _db, _config);
        var malformed = await InviteCodeEndpoints.CheckInviteCodeAsync(
            new InviteCodeEndpoints.InviteCodeRequest("nope"), AuthContext(), _db, _config);

        Assert.Equal(422, ((IStatusCodeHttpResult)unknown).StatusCode);
        Assert.Equal(422, ((IStatusCodeHttpResult)malformed).StatusCode);
    }

    [Fact]
    public async Task InternalEndpoints_FailClosedWithoutOrWithWrongToken()
    {
        var code = await IssueAsync();
        var unset = new ConfigurationBuilder().Build();

        var noConfig = await InviteCodeEndpoints.CheckInviteCodeAsync(
            new InviteCodeEndpoints.InviteCodeRequest(code), AuthContext(), _db, unset);
        var wrongToken = await InviteCodeEndpoints.RedeemInviteCodeAsync(
            new InviteCodeEndpoints.InviteCodeRequest(code), AuthContext("Bearer not-the-token"), _db, _config);

        Assert.Equal(401, ((IStatusCodeHttpResult)noConfig).StatusCode);
        Assert.Equal(401, ((IStatusCodeHttpResult)wrongToken).StatusCode);
        Assert.Null((await _db.InviteCodes.SingleAsync()).UsedAt);
    }

    [Fact]
    public void TtlDefaultsToSevenDaysAndHonoursConfig()
    {
        Assert.Equal(7, InviteCodeEndpoints.ReadTtlDays(new ConfigurationBuilder().Build()));
        var custom = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [InviteCodeEndpoints.TtlConfigKey] = "3" })
            .Build();
        Assert.Equal(3, InviteCodeEndpoints.ReadTtlDays(custom));
    }

    private async Task<string> IssueAsync()
    {
        var result = await InviteCodeEndpoints.IssueInviteCodeAsync(
            _companyId, RecruiterContext(Creator), _db, _config);
        return ((Microsoft.AspNetCore.Http.HttpResults.Created<InviteCodeEndpoints.IssuedInviteCodeResponse>)result).Value!.Code;
    }

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
}
