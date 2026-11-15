using System.Net;
using Job.Infrastructure.Clients;
using Microsoft.Extensions.Configuration;

namespace Job.Tests;

/// <summary>SRS AUTH-01-12 and AUTH-01-14: outbound link-company call to auth-svc.</summary>
public sealed class AuthCompanyLinkerTests
{
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid CompanyId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task NotConfigured_IsUnavailableAndMakesNoCall()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK);
        var linker = Build(handler, baseUrl: null, token: null);

        Assert.Equal(LinkCompanyResult.Unavailable, await linker.LinkCompanyAsync(UserId, CompanyId, default));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Success_PostsToLinkCompanyWithBearerAndBody()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK);
        var linker = Build(handler, "http://auth.internal/", "test-link-token");

        var result = await linker.LinkCompanyAsync(UserId, CompanyId, default);

        Assert.Equal(LinkCompanyResult.Linked, result);
        Assert.Equal(HttpMethod.Post, handler.Last!.Method);
        Assert.Equal($"http://auth.internal/api/internal/users/{UserId:D}/link-company", handler.Last.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.Last.Headers.Authorization!.Scheme);
        Assert.Equal("test-link-token", handler.Last.Headers.Authorization.Parameter);
        Assert.Contains(CompanyId.ToString(), handler.LastBody);
    }

    [Fact]
    public async Task Conflict_MapsToAlreadyHasCompany()
    {
        var linker = Build(new RecordingHandler(HttpStatusCode.Conflict), "http://auth.internal", "t");
        Assert.Equal(LinkCompanyResult.AlreadyHasCompany, await linker.LinkCompanyAsync(UserId, CompanyId, default));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task OtherFailures_AreUnavailable(HttpStatusCode status)
    {
        var linker = Build(new RecordingHandler(status), "http://auth.internal", "t");
        Assert.Equal(LinkCompanyResult.Unavailable, await linker.LinkCompanyAsync(UserId, CompanyId, default));
    }

    [Fact]
    public async Task NetworkFailure_IsUnavailable()
    {
        var linker = Build(new ThrowingHandler(), "http://auth.internal", "t");
        Assert.Equal(LinkCompanyResult.Unavailable, await linker.LinkCompanyAsync(UserId, CompanyId, default));
    }

    private static AuthCompanyLinker Build(HttpMessageHandler handler, string? baseUrl, string? token)
    {
        var values = new Dictionary<string, string?>();
        if (baseUrl is not null) values[AuthCompanyLinker.BaseUrlKey] = baseUrl;
        if (token is not null) values[AuthCompanyLinker.TokenKey] = token;
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return new AuthCompanyLinker(new HttpClient(handler), config);
    }

    private sealed class RecordingHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public HttpRequestMessage? Last { get; private set; }
        public string LastBody { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Last = request;
            LastBody = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(status);
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("connection refused");
    }
}
