using System.Net;
using System.Text;
using System.Text.Json;
using Job.Core.Entities;
using Job.Infrastructure.Clients;
using Job.Infrastructure.Data;
using Job.Infrastructure.Recommendations;
using Job.Infrastructure.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Job.Tests.Recommendations;

/// <summary>
/// PBL6-10 slice 6: one-time backfill of JobInteractions from app-svc
/// GET /api/internal/applications (0-based page, size 100, totalPages). The fake handler
/// returns JSON in the exact shape app-svc serves.
/// </summary>
public sealed class ApplicationBackfillServiceTests : IDisposable
{
    private const string Url = "http://app:5004";
    private const string Token = "internal-token";

    private readonly string _dbName = Guid.NewGuid().ToString();
    private readonly List<ServiceProvider> _providers = [];

    public void Dispose() => _providers.ForEach(p => p.Dispose());

    [Fact]
    public async Task ReadsEveryPage_AndStoresInteractions()
    {
        var a1 = Item();
        var a2 = Item();
        var a3 = Item();
        var handler = new FakeHandler(page => page switch
        {
            0 => Page([a1, a2], total: 3, page: 0, totalPages: 2),
            1 => Page([a3], total: 3, page: 1, totalPages: 2),
            _ => throw new InvalidOperationException("read past the last page"),
        });

        var report = await Run(handler);

        Assert.True(report.Completed);
        Assert.Equal(new[] { 0, 1 }, handler.Pages);
        Assert.All(handler.Requests, r =>
        {
            Assert.Equal("Bearer", r.Headers.Authorization?.Scheme);
            Assert.Equal(Token, r.Headers.Authorization?.Parameter);
            Assert.StartsWith($"{Url}/api/internal/applications?", r.RequestUri!.ToString());
            Assert.Contains("size=100", r.RequestUri!.Query);
        });
        Assert.Equal(3, report.Inserted);
        var rows = await Rows();
        Assert.Equal(3, rows.Count);
        var row = rows.Single(r => r.SourceId == a1.Id);
        Assert.Equal(a1.ApplicantId, row.UserId);
        Assert.Equal(a1.JobId, row.JobId);
        Assert.Equal(InteractionType.Applied, row.Type);
        Assert.Equal(a1.CreatedAt, row.OccurredAt);
    }

    [Fact]
    public async Task RunningTwice_DoesNotDuplicate()
    {
        var item = Item();
        var handler = new FakeHandler(_ => Page([item], total: 1, page: 0, totalPages: 1));

        await Run(handler);
        var second = await Run(handler);

        Assert.Equal(0, second.Inserted);
        Assert.Single(await Rows());
    }

    [Fact]
    public async Task InvalidItems_AreSkippedAndCounted()
    {
        var valid = Item();
        var body = $$"""
            {
              "items": [
                { "id": "{{valid.Id}}", "jobId": "{{valid.JobId}}", "applicantId": "{{valid.ApplicantId}}", "createdAt": "2026-11-01T08:00:00Z" },
                { "id": "{{Guid.NewGuid()}}", "jobId": "{{Guid.Empty}}", "applicantId": "{{Guid.NewGuid()}}", "createdAt": "2026-11-01T08:00:00Z" },
                { "id": "{{Guid.NewGuid()}}", "jobId": "{{Guid.NewGuid()}}", "applicantId": "{{Guid.NewGuid()}}", "createdAt": "yesterday" },
                { "id": "{{Guid.NewGuid()}}", "jobId": "{{Guid.NewGuid()}}" },
                "not an object"
              ],
              "total": 5, "page": 0, "size": 100, "totalPages": 1
            }
            """;

        var report = await Run(new FakeHandler(_ => Json(body)));

        Assert.True(report.Completed);
        Assert.Equal(1, report.Inserted);
        Assert.Equal(4, report.InvalidItems);
        Assert.Equal(valid.JobId, Assert.Single(await Rows()).JobId);
    }

    [Theory]
    [InlineData("""{ "total": 1, "page": 0, "size": 100, "totalPages": 1 }""")]
    [InlineData("""{ "items": [], "total": 1, "page": 0, "size": 100 }""")]
    [InlineData("""[ 1, 2, 3 ]""")]
    [InlineData("""<html>oops</html>""")]
    public async Task UnexpectedShape_StopsWithoutCrashing(string body)
    {
        var handler = new FakeHandler(_ => Json(body));

        var report = await Run(handler);

        Assert.False(report.Completed);
        Assert.Single(handler.Requests);
        Assert.Empty(await Rows());
    }

    [Fact]
    public async Task Unauthorized_StopsWithoutRetrying()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var report = await Run(handler);

        Assert.False(report.Completed);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Timeout_RetriesThreeTimesThenGivesUp()
    {
        var handler = new FakeHandler(_ => throw new TaskCanceledException("timeout"));

        var report = await Run(handler);

        Assert.False(report.Completed);
        Assert.Equal(1 + ApplicationBackfillService.MaxRetries, handler.Requests.Count);
    }

    [Fact]
    public async Task ServerError_IsRetriedAndCanRecover()
    {
        var item = Item();
        var calls = 0;
        var handler = new FakeHandler(_ => ++calls < 3
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : Page([item], total: 1, page: 0, totalPages: 1));

        var report = await Run(handler);

        Assert.True(report.Completed);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Single(await Rows());
    }

    [Fact]
    public async Task Disabled_MakesNoRequest()
    {
        var handler = new FakeHandler(_ => Page([Item()], total: 1, page: 0, totalPages: 1));

        var report = await Run(handler, enabled: false);

        Assert.False(report.Completed);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task MissingUrlOrToken_MakesNoRequest()
    {
        var handler = new FakeHandler(_ => Page([Item()], total: 1, page: 0, totalPages: 1));

        await Run(handler, url: null);
        await Run(handler, token: null);

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task HugeTotalPages_StopsAtSafetyCap()
    {
        var item = Item();
        var handler = new FakeHandler(page => Page([item], total: int.MaxValue, page: page, totalPages: int.MaxValue));

        var report = await Run(handler);

        Assert.False(report.Completed);
        Assert.Equal(ApplicationBackfillService.MaxPages, handler.Requests.Count);
    }

    [Fact]
    public async Task EmptyPage_EndsTheRun()
    {
        var handler = new FakeHandler(page => Page([], total: 0, page: page, totalPages: 5));

        var report = await Run(handler);

        Assert.True(report.Completed);
        Assert.Single(handler.Requests);
    }

    private async Task<BackfillReport> Run(
        FakeHandler handler, bool enabled = true, string? url = Url, string? token = Token)
    {
        var options = Options.Create(new RecommendationOptions
        {
            BackfillEnabled = enabled,
            AppServiceUrl = url,
            InternalServiceToken = token,
        });
        var provider = new ServiceCollection()
            .AddDbContext<JobDbContext>(o => o.UseInMemoryDatabase(_dbName))
            .AddScoped<InteractionWriter>()
            .AddSingleton(new AppServiceClient(new HttpClient(handler), options))
            .BuildServiceProvider();
        _providers.Add(provider);

        var service = new ApplicationBackfillService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            options,
            NullLogger<ApplicationBackfillService>.Instance)
        {
            RetryDelay = TimeSpan.Zero,
        };
        return await service.RunAsync(CancellationToken.None);
    }

    private async Task<List<JobInteraction>> Rows()
    {
        await using var db = new JobDbContext(
            new DbContextOptionsBuilder<JobDbContext>().UseInMemoryDatabase(_dbName).Options);
        return await db.JobInteractions.ToListAsync();
    }

    private sealed record ExportedApplication(Guid Id, Guid JobId, Guid ApplicantId, DateTime CreatedAt);

    private static ExportedApplication Item() =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new DateTime(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc));

    // Same JSON shape as app-svc InternalApplicationsPage (JsonSerializerDefaults.Web).
    private static HttpResponseMessage Page(ExportedApplication[] items, int total, int page, int totalPages) =>
        Json(JsonSerializer.Serialize(
            new { items, total, page, size = 100, totalPages },
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class FakeHandler(Func<int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        public List<int> Pages => Requests
            .Select(r => int.Parse(System.Web.HttpUtility.ParseQueryString(r.RequestUri!.Query)["page"]!))
            .ToList();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            var page = int.Parse(System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)["page"]!);
            return Task.FromResult(respond(page));
        }
    }
}
