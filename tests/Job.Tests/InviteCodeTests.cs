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

    [Theory]
    [InlineData("  Ann.Lee@Example.COM ", "ann.lee@example.com")]
    [InlineData("a@b.co", "a@b.co")]
    public void NormalizeEmail_TrimsAndLowerCases(string raw, string expected)
    {
        Assert.Equal(expected, InviteCodeFormat.NormalizeEmail(raw));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no-at-sign")]
    [InlineData("@example.com")]
    [InlineData("a@@example.com")]
    [InlineData("a@")]
    [InlineData("a b@example.com")]
    public void NormalizeEmail_RejectsMalformedAddresses(string? raw)
    {
        Assert.Null(InviteCodeFormat.NormalizeEmail(raw));
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
    private const string BoundEmail = "invitee@example.com";
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
            _companyId, Issue(BoundEmail), RecruiterContext(Creator), _db, _config);

        var created = Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.Created<InviteCodeEndpoints.IssuedInviteCodeResponse>>(result);
        var body = created.Value!;
        Assert.Matches("^[A-Z2-9]{4}-[A-Z2-9]{4}-[A-Z2-9]{4}$", body.Code);
        Assert.Equal(BoundEmail, body.Email);

        var stored = await _db.InviteCodes.SingleAsync();
        Assert.NotEqual(body.Code, stored.CodeHash);
        Assert.Equal(InviteCodeFormat.Hash(InviteCodeFormat.Normalize(body.Code)!), stored.CodeHash);
        Assert.Equal(_companyId, stored.CompanyId);
        Assert.Equal(Creator, stored.CreatedBy);
        Assert.Equal(BoundEmail, stored.Email);
        Assert.Equal(InviteCodeStatus.Active, stored.Status);
        Assert.Null(stored.UsedAt);
        Assert.Null(stored.RevokedAt);
        Assert.Null(stored.RevokeReason);
        var ttl = stored.ExpiresAt - DateTimeOffset.UtcNow;
        Assert.InRange(ttl.TotalDays, 6.99, 7.01);
    }

    [Fact]
    public async Task Issue_EmailIsRequiredAndNormalised()
    {
        var missing = await InviteCodeEndpoints.IssueInviteCodeAsync(
            _companyId, Issue(null), RecruiterContext(Creator), _db, _config);
        Assert.Equal(422, ((IStatusCodeHttpResult)missing).StatusCode);
        Assert.Empty(_db.InviteCodes);

        var mixedCase = await InviteCodeEndpoints.IssueInviteCodeAsync(
            _companyId, Issue("  Bob@Example.COM "), RecruiterContext(Creator), _db, _config);
        Assert.Equal("bob@example.com", ((Microsoft.AspNetCore.Http.HttpResults.Created<InviteCodeEndpoints.IssuedInviteCodeResponse>)mixedCase).Value!.Email);
    }

    [Fact]
    public async Task Issue_NonCreatorRecruiterIsForbidden()
    {
        var result = await InviteCodeEndpoints.IssueInviteCodeAsync(
            _companyId, Issue(BoundEmail), RecruiterContext(Guid.NewGuid()), _db, _config);

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
            legacy.Id, Issue(BoundEmail), RecruiterContext(Guid.Empty), _db, _config);

        Assert.Equal(403, ((IStatusCodeHttpResult)result).StatusCode);
    }

    [Fact]
    public async Task Issue_NonRecruiterIsForbidden()
    {
        var result = await InviteCodeEndpoints.IssueInviteCodeAsync(
            _companyId, Issue(BoundEmail), RecruiterContext(Creator, role: "User"), _db, _config);

        Assert.Equal(403, ((IStatusCodeHttpResult)result).StatusCode);
    }

    [Fact]
    public async Task Check_ValidCodeWithBoundEmailReturnsCompany_AndDoesNotConsume()
    {
        var code = await IssueAsync();

        var first = await Check(code, BoundEmail);
        var second = await Check(code, BoundEmail);

        Assert.Equal(200, ((IStatusCodeHttpResult)first).StatusCode);
        Assert.Equal(200, ((IStatusCodeHttpResult)second).StatusCode);
        Assert.Null((await _db.InviteCodes.SingleAsync()).UsedAt);
    }

    [Fact]
    public async Task Check_AndRedeem_WithAnotherEmail_AreRejectedWithTheSameError()
    {
        var code = await IssueAsync();

        var check = await Check(code, "someone-else@example.com");
        var redeem = await Redeem(code, "someone-else@example.com");

        Assert.Equal(422, ((IStatusCodeHttpResult)check).StatusCode);
        Assert.Equal(422, ((IStatusCodeHttpResult)redeem).StatusCode);
        Assert.Null((await _db.InviteCodes.SingleAsync()).UsedAt);
    }

    [Fact]
    public async Task Redeem_IsSingleUse_AndSecondAttemptGetsSameInvalidError()
    {
        var code = await IssueAsync();

        var first = await Redeem(code.ToLowerInvariant(), BoundEmail);
        var second = await Redeem(code, BoundEmail);

        Assert.Equal(200, ((IStatusCodeHttpResult)first).StatusCode);
        Assert.Equal(422, ((IStatusCodeHttpResult)second).StatusCode);
        Assert.NotNull((await _db.InviteCodes.SingleAsync()).UsedAt);
    }

    [Fact]
    public async Task Redeem_RequiresEmail()
    {
        var code = await IssueAsync();

        var result = await InviteCodeEndpoints.RedeemInviteCodeAsync(
            new InviteCodeEndpoints.InviteCodeRequest(code, null), AuthContext(), _db, _config);

        Assert.Equal(422, ((IStatusCodeHttpResult)result).StatusCode);
    }

    [Fact]
    public async Task Release_ClearsUsedSoTheCodeCanBeRedeemedAgain()
    {
        var code = await IssueAsync();
        var redeemed = await Redeem(code, BoundEmail);
        var inviteCodeId = Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.Ok<InviteCodeEndpoints.InviteCodeRedeemResponse>>(redeemed).Value!.InviteCodeId;

        var released = await InviteCodeEndpoints.ReleaseInviteCodeAsync(inviteCodeId, AuthContext(), _db, _config);
        var again = await Redeem(code, BoundEmail);

        Assert.Equal(204, ((IStatusCodeHttpResult)released).StatusCode);
        Assert.Equal(200, ((IStatusCodeHttpResult)again).StatusCode);
    }

    [Fact]
    public async Task Release_OfAPreviousAdminsCodeRevokesItInsteadOfReopeningIt()
    {
        var code = await IssueAsync();
        var redeemed = await Redeem(code, BoundEmail);
        var inviteCodeId = Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.Ok<InviteCodeEndpoints.InviteCodeRedeemResponse>>(redeemed).Value!.InviteCodeId;

        // The admin changes after the redeem (support approval) and before auth-svc releases the code.
        var company = await _db.Companies.SingleAsync();
        company.TransferAdmin(Guid.NewGuid());
        await _db.SaveChangesAsync();

        await InviteCodeEndpoints.ReleaseInviteCodeAsync(inviteCodeId, AuthContext(), _db, _config);

        var stored = await _db.InviteCodes.SingleAsync();
        Assert.Equal(InviteCodeStatus.Revoked, stored.Status);
        Assert.Equal(InviteCode.RevokeReasonAdminChanged, stored.RevokeReason);
        Assert.NotNull(stored.RevokedAt);
        Assert.Null(stored.UsedAt);
        Assert.Equal(422, ((IStatusCodeHttpResult)await Redeem(code, BoundEmail)).StatusCode);
    }

    [Fact]
    public async Task Check_ExpiredCodeIsInvalid()
    {
        var code = await IssueAsync();
        var entity = await _db.InviteCodes.SingleAsync();
        typeof(InviteCode).GetProperty(nameof(InviteCode.ExpiresAt))!
            .SetValue(entity, DateTimeOffset.UtcNow.AddMinutes(-1));
        await _db.SaveChangesAsync();

        var result = await Check(code, BoundEmail);

        Assert.Equal(422, ((IStatusCodeHttpResult)result).StatusCode);
    }

    [Fact]
    public async Task Check_RevokedCodeIsInvalid()
    {
        var code = await IssueAsync();
        var entity = await _db.InviteCodes.SingleAsync();
        Assert.True(entity.RevokeUnused(DateTimeOffset.UtcNow, InviteCode.RevokeReasonAdminChanged));
        await _db.SaveChangesAsync();

        var check = await Check(code, BoundEmail);
        var redeem = await Redeem(code, BoundEmail);

        Assert.Equal(422, ((IStatusCodeHttpResult)check).StatusCode);
        Assert.Equal(422, ((IStatusCodeHttpResult)redeem).StatusCode);
        Assert.Null((await _db.InviteCodes.SingleAsync()).UsedAt);
    }

    [Fact]
    public void RevokeUnused_LeavesUsedCodesAlone()
    {
        var entity = new InviteCode(_companyId, InviteCodeFormat.Hash("ABCDEFGHJKLM"), BoundEmail, Creator,
            DateTimeOffset.UtcNow, TimeSpan.FromDays(7));
        entity.MarkUsed(DateTimeOffset.UtcNow);

        Assert.False(entity.RevokeUnused(DateTimeOffset.UtcNow, InviteCode.RevokeReasonAdminChanged));
        Assert.Equal(InviteCodeStatus.Active, entity.Status);
    }

    [Fact]
    public async Task Check_UnknownAndMalformedCodesGiveTheSameError()
    {
        await IssueAsync();

        var unknown = await Check("ZZZZ-ZZZZ-ZZZZ", BoundEmail);
        var malformed = await Check("nope", BoundEmail);

        Assert.Equal(422, ((IStatusCodeHttpResult)unknown).StatusCode);
        Assert.Equal(422, ((IStatusCodeHttpResult)malformed).StatusCode);
    }

    [Fact]
    public async Task InternalEndpoints_FailClosedWithoutOrWithWrongToken()
    {
        var code = await IssueAsync();
        var unset = new ConfigurationBuilder().Build();

        var noConfig = await InviteCodeEndpoints.CheckInviteCodeAsync(
            new InviteCodeEndpoints.InviteCodeRequest(code, BoundEmail), AuthContext(), _db, unset);
        var wrongToken = await InviteCodeEndpoints.RedeemInviteCodeAsync(
            new InviteCodeEndpoints.InviteCodeRequest(code, BoundEmail), AuthContext("Bearer not-the-token"), _db, _config);

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

    private async Task<string> IssueAsync(string email = BoundEmail)
    {
        var result = await InviteCodeEndpoints.IssueInviteCodeAsync(
            _companyId, Issue(email), RecruiterContext(Creator), _db, _config);
        return ((Microsoft.AspNetCore.Http.HttpResults.Created<InviteCodeEndpoints.IssuedInviteCodeResponse>)result).Value!.Code;
    }

    private Task<IResult> Check(string code, string email) =>
        InviteCodeEndpoints.CheckInviteCodeAsync(
            new InviteCodeEndpoints.InviteCodeRequest(code, email), AuthContext(), _db, _config);

    private Task<IResult> Redeem(string code, string email) =>
        InviteCodeEndpoints.RedeemInviteCodeAsync(
            new InviteCodeEndpoints.InviteCodeRequest(code, email), AuthContext(), _db, _config);

    private static InviteCodeEndpoints.IssueInviteCodeRequest Issue(string? email) => new(email);

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
