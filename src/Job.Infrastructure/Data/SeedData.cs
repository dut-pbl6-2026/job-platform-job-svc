using Job.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Job.Infrastructure.Data;

public static class SeedData
{
    private static readonly SeedCategory[] Categories =
    [
        new(new Guid("7d09cd31-5580-41a2-948e-00bfbfdc8e3b"), "IT", "Software, data, security, and technology roles"),
        new(new Guid("9e9769ef-0457-41fa-86be-7ea16a7259db"), "Finance", "Accounting, banking, audit, and finance roles"),
        new(new Guid("946e5b41-d01a-4e98-81da-8593f0105bf1"), "Marketing", "Brand, digital marketing, communications, and growth roles"),
        new(new Guid("50d302a4-bb71-45f7-a160-e0f33e6574de"), "Healthcare", "Clinical, medical, pharmacy, and healthcare operations roles"),
        new(new Guid("6fa96f61-6748-489d-83a1-684a7c2a9814"), "Education", "Teaching, academic, tutoring, and education operations roles"),
        new(new Guid("73f4d2ea-9801-43d6-95bb-f6bec8cba662"), "Engineering", "Mechanical, electrical, civil, and industrial engineering roles"),
        new(new Guid("c50e82d1-fd84-4451-a7a8-c4397ed7b688"), "Sales", "Business development, account management, and sales roles"),
        new(new Guid("de661c63-87e6-4f0a-94f6-f86a74f116ce"), "Hospitality", "Hotel, restaurant, tourism, and service roles"),
        new(new Guid("be6d8663-9586-42bb-868e-32af6cd28124"), "Others", "General roles that do not fit predefined categories")
    ];

    public static async Task SeedCategoriesAsync(JobDbContext db, CancellationToken cancellationToken = default)
    {
        var existingNames = await db.Categories
            .Select(x => x.Name)
            .ToListAsync(cancellationToken);
        var existingNameSet = existingNames.ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var category in Categories.Where(x => !existingNameSet.Contains(x.Name)))
        {
            db.Categories.Add(Category.Seed(category.Id, category.Name, category.Description));
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private sealed record SeedCategory(Guid Id, string Name, string Description);
}
