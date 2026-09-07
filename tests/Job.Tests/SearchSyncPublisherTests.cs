using System.Net;
using System.Text.Json;
using Job.Api.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Job.Tests;

/// <summary>
/// Best-effort contract tests for <see cref="SearchSyncPublisher"/> (PBL6-19):
/// sync must never throw, must target the merged index contract, and must be
/// silent when unconfigured. Uses a capturing HttpMessageHandler — no network.
/// </summary>
public class SearchSyncPublisherTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();
        public List<string?> RequestBodies { get; } = new();
        public HttpResponseMessage Response { get; set; } = new(HttpStatusCode.OK);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            // Buffer now: the publisher disposes the request on return.
            RequestBodies.Add(request.Content is null ? null : await request.Content.ReadAsStringAsync(ct));
            return Response;
        }
    }

    private static JobSyncPayload SamplePayload(string status = "Active") =>
        SearchSyncPublisher.BuildDocument(
            Guid.NewGuid(), "Backend Dev", "Build APIs", Guid.NewGuid(), "Acme",
            "Da Nang", 10_000_000, 20_000_000, "VND", null, null, null, null,
            "FullTime", "Entry", Guid.NewGuid(), status);

    private static SearchSyncPublisher CreatePublisher(
        CapturingHandler handler, string? syncUrl, string? token = null)
    {
        var values = new Dictionary<string, string?>();
        if (syncUrl is not null)
        {
            values["SEARCH_SYNC_URL"] = syncUrl;
        }

        if (token is not null)
        {
            values["SEARCH_INDEX_TOKEN"] = token;
        }

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
        return new SearchSyncPublisher(
            new HttpClient(handler), config, NullLogger<SearchSyncPublisher>.Instance);
    }

    [Fact]
    public async Task PublishUpsertAsync_PostsToIndexEndpoint()
    {
        var handler = new CapturingHandler();
        var publisher = CreatePublisher(handler, "http://search:5003");

        await publisher.PublishUpsertAsync(SamplePayload());

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("http://search:5003/api/search/index", request.RequestUri!.ToString());
    }

    [Fact]
    public async Task PublishUpsertAsync_SendsIndexTokenWhenConfigured()
    {
        var handler = new CapturingHandler();
        var publisher = CreatePublisher(handler, "http://search:5003", token: "s3cret");

        await publisher.PublishUpsertAsync(SamplePayload());

        var request = Assert.Single(handler.Requests);
        Assert.True(request.Headers.TryGetValues("X-Internal-Token", out var values));
        Assert.Equal("s3cret", Assert.Single(values));
    }

    [Fact]
    public async Task PublishUpsertAsync_SendsActualStatus_NotDefault()
    {
        // Regression test for review B-3: a Closed job must not be re-indexed as Active.
        var handler = new CapturingHandler();
        var publisher = CreatePublisher(handler, "http://search:5003");

        await publisher.PublishUpsertAsync(SamplePayload(status: "Closed"));

        var request = Assert.Single(handler.Requests);
        var json = Assert.Single(handler.RequestBodies);
        Assert.NotNull(json);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("Closed", doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task PublishDeleteAsync_DeletesById()
    {
        var handler = new CapturingHandler();
        var publisher = CreatePublisher(handler, "http://search:5003");
        var id = Guid.NewGuid();

        await publisher.PublishDeleteAsync(id);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Delete, request.Method);
        Assert.Equal($"http://search:5003/api/search/index/{id}", request.RequestUri!.ToString());
    }

    [Fact]
    public async Task Publish_DoesNotThrow_WhenSearchIsDown()
    {
        var handler = new CapturingHandler
        {
            Response = new HttpResponseMessage(HttpStatusCode.InternalServerError),
        };
        var publisher = CreatePublisher(handler, "http://search:5003");

        // Best-effort: HTTP 500 and transport errors must never propagate.
        await publisher.PublishUpsertAsync(SamplePayload());
        await publisher.PublishDeleteAsync(Guid.NewGuid());
    }

    [Fact]
    public async Task Publish_SkipsSilently_WhenUrlNotConfigured()
    {
        var handler = new CapturingHandler();
        var publisher = CreatePublisher(handler, syncUrl: null);

        await publisher.PublishUpsertAsync(SamplePayload());
        await publisher.PublishDeleteAsync(Guid.NewGuid());

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void BuildDocument_PreservesAllFields()
    {
        var jobId = Guid.NewGuid();
        var companyId = Guid.NewGuid();
        var recruiterId = Guid.NewGuid();

        var payload = SearchSyncPublisher.BuildDocument(
            jobId, "T", "D", companyId, "Acme", "HN",
            1, 2, "VND", null, "IT", "Req", "Ben", "FullTime", "Entry",
            recruiterId, "Closed");

        Assert.Equal(jobId.ToString(), payload.Id);
        Assert.Equal(companyId.ToString(), payload.CompanyId);
        Assert.Equal(recruiterId.ToString(), payload.RecruiterId);
        Assert.Equal("Closed", payload.Status);
        Assert.Equal("Acme", payload.CompanyName);
    }
}
