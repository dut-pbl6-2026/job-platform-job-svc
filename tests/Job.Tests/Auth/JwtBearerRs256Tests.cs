using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Job.Api.Auth;
using Job.Tests.Auth;
using Xunit;

namespace Job.Tests.Auth;

/// <summary>
/// The RS256 validator in isolation: a minimal host with one protected route. Covers the accept path
/// (JWKS-published key) and every refusal the service must make.
/// </summary>
public sealed class JwtBearerRs256Tests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync() => await StartAsync(healthy: true);

    public Task DisposeAsync()
    {
        _client.Dispose();
        return _app.DisposeAsync().AsTask();
    }

    [Fact]
    public async Task ValidRs256TokenFromJwks_IsAccepted()
    {
        var response = await GetProtectedAsync(Rs256Token());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Hs256Token_IsRejected()
    {
        var key = new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(48));
        var token = Sign(new SigningCredentials(key, SecurityAlgorithms.HmacSha256), kid: null);

        Assert.Equal(HttpStatusCode.Unauthorized, (await GetProtectedAsync(token)).StatusCode);
    }

    [Fact]
    public async Task Rs256TokenFromUnknownKey_IsRejected()
    {
        var foreign = new RsaSecurityKey(TestSigningKeys.Foreign) { KeyId = TestSigningKeys.Kid };

        Assert.Equal(HttpStatusCode.Unauthorized, (await GetProtectedAsync(Sign(new SigningCredentials(foreign, SecurityAlgorithms.RsaSha256), TestSigningKeys.Kid))).StatusCode);
    }

    [Fact]
    public async Task Rs256TokenWithoutKid_IsRejected()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetProtectedAsync(Rs256Token(kid: null))).StatusCode);
    }

    [Fact]
    public async Task ValidTokenWhileJwksUnavailable_IsRejected()
    {
        await _app.DisposeAsync();
        _client.Dispose();
        await StartAsync(healthy: false);

        Assert.Equal(HttpStatusCode.Unauthorized, (await GetProtectedAsync(Rs256Token())).StatusCode);
    }

    [Fact]
    public async Task MissingMetadataAddress_FailsStartup()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Configuration["Jwt:MetadataAddress"] = "";

        var ex = Assert.Throws<InvalidOperationException>(() => builder.Services.AddRs256JwtBearer(builder.Configuration));

        Assert.Contains("Jwt:MetadataAddress", ex.Message);
    }

    private async Task StartAsync(bool healthy)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Configuration["Jwt:MetadataAddress"] = TestSigningKeys.MetadataAddress;
        builder.Services.AddRs256JwtBearer(builder.Configuration);
        builder.Services.AddAuthorization();
        var handler = new JwksStubHandler(healthy);
        builder.Services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme,
            o => o.ConfigurationManager = handler.CreateConfigurationManager());
        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapGet("/protected", () => Results.Ok()).RequireAuthorization();
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    private Task<HttpResponseMessage> GetProtectedAsync(string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/protected");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return _client.SendAsync(request);
    }

    private static string Rs256Token(string? kid = TestSigningKeys.Kid) =>
        Sign(new SigningCredentials(new RsaSecurityKey(TestSigningKeys.Current) { KeyId = kid }, SecurityAlgorithms.RsaSha256), kid);

    private static string Sign(SigningCredentials credentials, string? kid)
    {
        var claims = new[] { new Claim(JwtRegisteredClaimNames.Sub, Guid.NewGuid().ToString()) };
        var header = new System.IdentityModel.Tokens.Jwt.JwtHeader(credentials);
        if (kid is null)
            header.Remove("kid");
        var token = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(header, new System.IdentityModel.Tokens.Jwt.JwtPayload("job-platform", "job-platform", claims,
            DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(10)));
        return new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().WriteToken(token);
    }
}
