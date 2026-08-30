using Job.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Job.Api.Endpoints;

/// <summary>
/// Category endpoints per SRS JOB-01-06.
/// GET /api/categories — public, no auth required
/// </summary>
public static class CategoryEndpoints
{
    public static WebApplication MapCategoryEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/categories").WithTags("Categories");

        // GET /api/categories — JOB-01-06: public, returns all predefined categories
        group.MapGet("/", async (JobDbContext db) =>
        {
            var categories = await db.Categories
                .OrderBy(c => c.Name)
                .Select(c => new { c.Id, c.Name, c.Description })
                .ToListAsync();

            return Results.Ok(categories);
        });

        return app;
    }
}
