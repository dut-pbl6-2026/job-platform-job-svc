using Job.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// MAINT-06: structured JSON logging (ILogger JSON format ERROR/WARN/INFO/DEBUG)
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(o =>
{
    o.IncludeScopes = true;
    o.TimestampFormat = "yyyy-MM-ddTHH:mm:ssZ";
});

// PORT-05: connection string from env — no hardcoded values (PORT-02/SEC-08)
var conn = builder.Configuration.GetConnectionString("JobDb")
           ?? builder.Configuration["DATABASE_URL_JOB"]
           ?? throw new InvalidOperationException(
               "Connection string not configured. Set DATABASE_URL_JOB env var or ConnectionStrings:JobDb in appsettings.");

builder.Services.AddDbContext<JobDbContext>(o => o.UseNpgsql(conn));

// REL-07: ProblemDetails for RFC 7807 error responses (7-eir.md:7.7.1)
builder.Services.AddProblemDetails();

// MAINT-03: OpenAPI 3.0 (7-eir.md:7.5.3)
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.SwaggerDoc("v1", new() { Title = "Job Service", Version = "v0.1.0" });
});

var app = builder.Build();

// REL-07: UseExceptionHandler maps unhandled exceptions → ProblemDetails JSON
app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// REL-06: health check endpoint per service (6-nfr.md:REL-06, 8-system-architecture.md)
app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "job" }))
   .WithTags("Health")
   .ExcludeFromDescription();

app.MapGet("/", () => Results.Ok(new { service = "job", version = "0.1.0" }))
   .ExcludeFromDescription();

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
