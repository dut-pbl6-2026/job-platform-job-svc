using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Job.Api.Auth;
using Job.Api.Middleware;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;

namespace Job.Tests;

/// <summary>
/// The development header auth is opt-in: Development alone must not register DevAuthMiddleware.
/// Mutates the process environment variable, so this collection never runs in parallel.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DevAuthEnvironmentCollection
{
    public const string Name = "DevAuthEnvironment";
}

[Collection(DevAuthEnvironmentCollection.Name)]
public sealed class DevAuthRegistrationTests : IAsyncLifetime
{
    private readonly string? _originalEnvValue = Environment.GetEnvironmentVariable(DevAuthPolicy.SettingKey);
    private readonly List<WebApplication> _apps = [];

    public Task InitializeAsync()
    {
        // The flag may be absent from config but still present as an env var on the host running the suite.
        Environment.SetEnvironmentVariable(DevAuthPolicy.SettingKey, null);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var app in _apps)
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
        Environment.SetEnvironmentVariable(DevAuthPolicy.SettingKey, _originalEnvValue);
    }

    [Fact]
    public async Task Development_WithoutFlag_DoesNotRegisterDevAuth()
    {
        var client = await StartAsync(Environments.Development, flag: null);

        using var response = await SendAsAsync(client, Guid.NewGuid(), "Admin");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Development_WithFlagFalse_DoesNotRegisterDevAuth()
    {
        var client = await StartAsync(Environments.Development, flag: "false");

        using var response = await SendAsAsync(client, Guid.NewGuid(), "Admin");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Development_WithFlagTrue_RegistersDevAuthAndTrustsHeaders()
    {
        var userId = Guid.NewGuid();
        var companyId = Guid.NewGuid();
        var client = await StartAsync(Environments.Development, flag: "true");

        using var response = await SendAsAsync(client, userId, "Recruiter", companyId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(userId, json.GetProperty("id").GetGuid());
        Assert.Equal("Recruiter", json.GetProperty("role").GetString());
        Assert.Equal(companyId, json.GetProperty("company").GetGuid());
    }

    [Fact]
    public async Task NonDevelopment_WithFlagTrue_DoesNotRegisterDevAuth()
    {
        var client = await StartAsync(Environments.Production, flag: "true");

        using var response = await SendAsAsync(client, Guid.NewGuid(), "Admin");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private async Task<HttpClient> StartAsync(string environment, string? flag)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.WebHost.UseTestServer();
        if (flag is not null)
            builder.Configuration[DevAuthPolicy.SettingKey] = flag;

        var app = builder.Build();
        app.UseDevAuthIfEnabled();
        app.MapGet("/whoami", (HttpContext ctx) =>
        {
            var id = ctx.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (id is null)
                return Results.StatusCode(StatusCodes.Status401Unauthorized);

            return Results.Ok(new
            {
                id,
                role = ctx.User.FindFirst(ClaimTypes.Role)?.Value,
                company = ctx.User.FindFirst(IdentityHelper.CompanyIdClaimType)?.Value,
            });
        });

        await app.StartAsync();
        _apps.Add(app);
        return app.GetTestClient();
    }

    private static Task<HttpResponseMessage> SendAsAsync(
        HttpClient client, Guid userId, string role, Guid? companyId = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/whoami");
        request.Headers.Add("X-User-Id", userId.ToString());
        request.Headers.Add("X-User-Role", role);
        if (companyId is { } company)
            request.Headers.Add("X-User-Company", company.ToString());
        return client.SendAsync(request);
    }
}
