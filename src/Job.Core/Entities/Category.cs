using SharedKernel;

namespace Job.Core.Entities;

public class Category : Entity
{
    private static readonly DateTime SeedTimestamp = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public string Name { get; private set; } = "";
    public string? Description { get; private set; }

    private Category() { }

    public Category(string name, string? description = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name, nameof(name));
        Name = name.Trim();
        Description = description?.Trim();
    }

    public static Category Seed(Guid id, string name, string? description = null)
    {
        var category = new Category(name, description)
        {
            Id = id,
            CreatedAt = SeedTimestamp,
            UpdatedAt = SeedTimestamp
        };

        return category;
    }

    public void Update(string name, string? description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name, nameof(name));
        Name = name.Trim();
        Description = description?.Trim();
        Touch();
    }
}
