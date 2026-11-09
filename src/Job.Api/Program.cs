using System.Text;
using Job.Api.Endpoints;
using Job.Api.Health;
using Job.Api.Observability;
using Job.Api.Middleware;
using Job.Api.Services;
using Job.Infrastructure.Data;
using Job.Infrastructure.Recommendations;
using Job.Infrastructure.Workers;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using SharedKernel;
using SharedKernel.Kafka;
using OpenTelemetry.Metrics;

var builder = WebApplication.CreateBuilder(args);

// MAINT-06: Serilog structured logging + correlation id (PBL6-49)
builder.AddObservability();

// PORT-05: connection string from env — no hardcoded values (PORT-02/SEC-08)
// appsettings*.json ship a "<set via ...>" placeholder for ConnectionStrings:JobDb; skip it
// (and blanks) so DATABASE_URL_JOB is used instead of handing the placeholder to Npgsql.
static string? Usable(string? value) =>
    string.IsNullOrWhiteSpace(value) || value.TrimStart().StartsWith('<') ? null : value;

var conn = Usable(builder.Configuration.GetConnectionString("JobDb"))
           ?? Usable(builder.Configuration["DATABASE_URL_JOB"]);

// `dotnet ef` design-time commands (e.g. has-pending-model-changes in CI / mise ef-check) build the
// host without DB env vars and never open a connection — register Npgsql without a connection string.
if (conn is null && !EF.IsDesignTime)
    throw new InvalidOperationException(
        "Connection string not configured. Set DATABASE_URL_JOB env var or ConnectionStrings:JobDb in appsettings.");

builder.Services.AddDbContext<JobDbContext>(o =>
{
    if (conn is null) o.UseNpgsql();
    else o.UseNpgsql(conn);
});
builder.Services.AddJobHealthChecks();

// === Auth config — Production uses JWT Bearer; Dev uses DevAuthMiddleware ===
// (git-strategy AGENTS.md JWT section)
if (!builder.Environment.IsDevelopment())
{
    var jwtSection = builder.Configuration.GetSection(JwtOptions.SectionName);
    var jwt = jwtSection.Get<JwtOptions>() ?? new JwtOptions();

    // SEC-08: never hardcode secret in prod — fail fast if not configured
    if (string.IsNullOrEmpty(jwt.Secret) || jwt.Secret.Length < 32)
        throw new InvalidOperationException(
            "JWT secret not configured or too short. Set JWT_SECRET env var (min 32 chars) or Jwt:Secret in configuration.");

    builder.Services
        .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(o =>
        {
            o.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = jwt.Issuer,
                ValidAudience = jwt.Audience,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Secret)),
                ClockSkew = TimeSpan.Zero // SEC-01
            };
        });
    builder.Services.AddAuthorization();
}

// Monitoring: expose OpenTelemetry metrics on /metrics (Prometheus scrape target).
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics =>
    {
        metrics
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddRuntimeInstrumentation()
            .AddPrometheusExporter();
    });

// REL-07: ProblemDetails for RFC 7807 error responses (7-eir.md:7.7.1)
builder.Services.AddProblemDetails();

// PBL6-19: search sync publisher (direct HTTP to search-svc; disabled when SEARCH_SYNC_URL unset).
// Also used as HTTP fallback when Kafka circuit-breaker is open (plan 2.3).
builder.Services.AddHttpClient<SearchSyncPublisher>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(5);
});

// PBL6-5: Kafka event publishing (job.created/updated/deleted -> job-events topic).
// KafkaOptions binds KAFKA_BOOTSTRAP_SERVERS env var; empty = Kafka disabled (HTTP-only sync).
builder.Services.Configure<KafkaOptions>(o =>
{
    o.BootstrapServers = builder.Configuration["KAFKA_BOOTSTRAP_SERVERS"]
        ?? builder.Configuration["Kafka:BootstrapServers"] ?? "";
    o.SaslUsername = builder.Configuration["KAFKA_SASL_USERNAME"]
        ?? builder.Configuration["Kafka:SaslUsername"] ?? "";
    o.SaslPassword = builder.Configuration["KAFKA_SASL_PASSWORD"]
        ?? builder.Configuration["Kafka:SaslPassword"] ?? "";
});

var kafkaServers = builder.Configuration["KAFKA_BOOTSTRAP_SERVERS"]
    ?? builder.Configuration["Kafka:BootstrapServers"] ?? "";

if (!string.IsNullOrWhiteSpace(kafkaServers))
{
    // Kafka configured: register real producer (Singleton — one underlying TCP connection).
    builder.Services.AddSingleton<IKafkaProducer, KafkaProducerService>();
}
else
{
    // Kafka not configured: register a no-op producer. Publisher will always HTTP-fallback.
    builder.Services.AddSingleton<IKafkaProducer, NoOpKafkaProducer>();
}

// Singleton: circuit-breaker state must persist across requests (plan v3.1).
builder.Services.AddSingleton<KafkaJobEventPublisher>();
builder.Services.AddHostedService<KafkaHealthCheckService>();
builder.Services.AddHostedService<OutboxPublisherService>();

// PBL6-10: recommendation read model. The consumer runs only when Kafka is configured.
builder.Services.AddScoped<InteractionWriter>();
if (!string.IsNullOrWhiteSpace(kafkaServers))
{
    builder.Services.AddHostedService<ApplicationInteractionsConsumer>();
}

// MAINT-03: OpenAPI 3.0 (7-eir.md:7.5.3)
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.SwaggerDoc("v1", new() { Title = "Job Service", Version = "v0.1.0" });
    o.AddSecurityDefinition("X-User-Id", new Microsoft.OpenApi.OpenApiSecurityScheme
    {
        In = Microsoft.OpenApi.ParameterLocation.Header,
        Name = "X-User-Id",
        Type = Microsoft.OpenApi.SecuritySchemeType.ApiKey,
        Description = "Dev-only: paste a UUID here (also add X-User-Role header manually)"
    });
    o.AddSecurityDefinition("X-User-Role", new Microsoft.OpenApi.OpenApiSecurityScheme
    {
        In = Microsoft.OpenApi.ParameterLocation.Header,
        Name = "X-User-Role",
        Type = Microsoft.OpenApi.SecuritySchemeType.ApiKey,
        Description = "Dev-only: Recruiter | User (gateway forwards X-User-Id / X-User-Role per GW-01)"
    });
});

var app = builder.Build();

app.UseObservability();

// REL-07: UseExceptionHandler maps unhandled exceptions → ProblemDetails JSON
app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
    // feat(job): add DevAuthMiddleware — dev-only header auth (no JWT needed for Postman testing)
    app.UseMiddleware<DevAuthMiddleware>();
}
else
{
    app.UseAuthentication(); // real JWT Bearer
    app.UseAuthorization();
}

// Monitoring: /metrics is meant for the internal scrape network; keep it off in
// Production unless METRICS_ENABLED=true (all services are publicly reachable).
if (app.Configuration.GetValue("METRICS_ENABLED", !app.Environment.IsProduction()))
{
    app.MapPrometheusScrapingEndpoint();
}

// REL-06: health check endpoint per service (6-nfr.md:REL-06, 8-system-architecture.md)
app.MapJobHealth();

app.MapGet("/", () => Results.Ok(new { service = "job", version = "0.1.0" }))
   .ExcludeFromDescription();

// Job CRUD endpoints (SRS JOB-01-01 to JOB-01-05)
app.MapJobEndpoints();
app.MapAdminJobEndpoints();
app.MapInternalStatsEndpoints();

// PBL6-19 (S-2): surface search-sync wiring once at startup — a missing
// SEARCH_SYNC_URL silently disables indexing, which must never go unnoticed.
if (string.IsNullOrWhiteSpace(app.Configuration["SEARCH_SYNC_URL"] ?? app.Configuration["SearchSync:Url"]))
{
    app.Logger.LogWarning("SEARCH_SYNC_URL is not set. Search index sync is DISABLED — jobs will not appear in search results.");
}

// PBL6-5: Kafka sync status at startup
if (string.IsNullOrWhiteSpace(kafkaServers))
{
    app.Logger.LogWarning(
        "KAFKA_BOOTSTRAP_SERVERS is not set. Kafka publishing is DISABLED — using HTTP sync only.");
    app.Logger.LogWarning(
        "Outbox events (admin approve/reject) cannot be published without Kafka and will exhaust retries.");
}
else
{
    app.Logger.LogInformation(
        "Kafka publishing enabled. BootstrapServers={Servers}, Topic=job-events.",
        kafkaServers);
}

// feat(admin): approval workflow is opt-in so existing recruiter flows stay Active by default.
var adminApprovalEnabled =
    bool.TryParse(app.Configuration["FEATURE_ADMIN_APPROVAL"], out var approvalFlag) && approvalFlag;
app.Logger.LogInformation(
    "Admin approval flow {State}.",
    adminApprovalEnabled
        ? "ENABLED — new recruiter jobs start as Pending"
        : "DISABLED — new recruiter jobs start as Active");

// Category endpoints (SRS JOB-01-06)
app.MapCategoryEndpoints();

// Company endpoints (SRS D.1 line 220-222)
app.MapCompanyEndpoints();

// REL-01: auto-migrate on startup with fail-fast (no swallow in any environment)
using (var scope = app.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
    var db = scope.ServiceProvider.GetRequiredService<JobDbContext>();

    try
    {
        await db.Database.MigrateAsync();
        await SeedData.SeedCategoriesAsync(db);
        logger.LogInformation("DB migrated and categories seeded");
    }
    catch (Exception ex)
    {
        // M4/REL-01: Fail-fast — unmigrated DB → every request 500.
        // Always throw; container orchestrator will restart with correct state.
        logger.LogError(ex, "DB migrate/seed failed — shutting down");
        throw;
    }
}

app.Run();
