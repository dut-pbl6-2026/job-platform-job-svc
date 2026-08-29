using Job.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var conn = builder.Configuration.GetConnectionString("JobDb")
           ?? builder.Configuration["DATABASE_URL_JOB"]
           ?? builder.Configuration["ConnectionStrings:JobDb"];

if (string.IsNullOrWhiteSpace(conn))
{
    throw new InvalidOperationException(
        "Missing JobDb connection string. Configure ConnectionStrings:JobDb or DATABASE_URL_JOB.");
}

builder.Services.AddDbContext<JobDbContext>(o => o.UseNpgsql(conn));

builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "job" }));
app.MapGet("/", () => Results.Ok(new { service = "job", version = "0.1.0" }));

using (var scope = app.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Program");
    var db = scope.ServiceProvider.GetRequiredService<JobDbContext>();

    try
    {
        await db.Database.MigrateAsync();
        await SeedData.SeedCategoriesAsync(db);
        logger.LogInformation("DB migrated and categories seeded");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "DB migrate/seed failed");
        if (app.Environment.IsDevelopment())
        {
            throw;
        }
    }
}

app.Run();
