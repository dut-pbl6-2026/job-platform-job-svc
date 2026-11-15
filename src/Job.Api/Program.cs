using System.Text;
using Job.Api.Bootstrap;
using Job.Api.Endpoints;
using Job.Api.Health;
using Job.Api.Observability;
using Job.Api.Middleware;
using Job.Api.Services;
using Job.Infrastructure.Clients;
using Job.Infrastructure.Data;
using Job.Infrastructure.Observability;
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

// Schema migration is a separate deploy step: `migrate` applies pending migrations, seeds categories and exits.
// The web host never migrates on startup. Checked before any other service registration.
if (MigrateCommand.IsInvoked(args))
    return await MigrateCommand.RunAsync(args, conn, Console.Out, Console.Error);

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
            .AddMeter(RecommendationMetrics.MeterName) // PBL6-10
            .AddPrometheusExporter();
    });

// REL-07: ProblemDetails for RFC 7807 error responses (7-eir.md:7.7.1)
builder.Services.AddProblemDetails();

// PBL6-5: Kafka event publishing (job.created/updated/deleted -> job-events topic), sent by
// OutboxPublisherService. KafkaOptions binds KAFKA_BOOTSTRAP_SERVERS env var; empty = Kafka disabled.
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
    // Kafka not configured: register a no-op producer that fails every publish, so outbox rows stay
    // queued (and are logged at Error) until Kafka is configured.
    builder.Services.AddSingleton<IKafkaProducer, NoOpKafkaProducer>();
}

builder.Services.AddHostedService<OutboxPublisherService>();

// PBL6-10: recommendation read model. The consumer runs only when Kafka is configured.
builder.Services.AddRecommendations(builder.Configuration);
if (!string.IsNullOrWhiteSpace(kafkaServers))
{
    builder.Services.AddHostedService<ApplicationInteractionsConsumer>();
}

// Per-caller tokens: job-svc accepts only the token issued to app-svc (/api/internal/jobs/{id}) and
// to auth-svc (/api/internal/stats). Outside Development a missing value fails startup.
if (!builder.Environment.IsDevelopment() &&
    (string.IsNullOrWhiteSpace(builder.Configuration[InternalJobEndpoints.AppCallerTokenKey]) ||
     string.IsNullOrWhiteSpace(builder.Configuration[InternalStatsEndpoints.AuthCallerTokenKey])))
{
    throw new InvalidOperationException(
        "JOB_INTERNAL_TOKEN_FOR_APP and JOB_INTERNAL_TOKEN_FOR_AUTH are required outside Development");
}

// One-time startup backfill from app-svc; no-op unless RECOMMENDATION_BACKFILL_ENABLED=true.
builder.Services.AddHttpClient<AppServiceClient>(client => client.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddHostedService<ApplicationBackfillService>();

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
app.MapRecommendationEndpoints();
app.MapAdminJobEndpoints();
app.MapInternalStatsEndpoints();
app.MapInternalJobEndpoints();

// PBL6-5: Kafka status at startup. Job events wait in the outbox until Kafka is configured.
if (string.IsNullOrWhiteSpace(kafkaServers))
{
    app.Logger.LogWarning(
        "KAFKA_BOOTSTRAP_SERVERS is not set. Job events stay in the outbox (unpublished) until Kafka is configured.");
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

app.Run();

return 0;
