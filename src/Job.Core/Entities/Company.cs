using SharedKernel;

namespace Job.Core.Entities;

public class Company : Entity
{
    public string Name { get; private set; } = "";
    public string? TaxCode { get; private set; }
    public bool Verified { get; private set; }
    public string? LogoUrl { get; private set; }
    public string? Website { get; private set; }
    public string? Description { get; private set; }
    public string? Address { get; private set; }
    public string? Industry { get; private set; }
    public string? Size { get; private set; }

    private Company() { }

    public Company(string name, string? taxCode = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name, nameof(name));
        Name = name.Trim();
        TaxCode = NormalizeOptional(taxCode);
    }

    public void Update(
        string name,
        string? taxCode,
        string? logoUrl,
        string? website,
        string? description,
        string? address,
        string? industry,
        string? size)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name, nameof(name));
        Name = name.Trim();
        TaxCode = NormalizeOptional(taxCode);
        LogoUrl = NormalizeOptional(logoUrl);
        Website = NormalizeOptional(website);
        Description = NormalizeOptional(description);
        Address = NormalizeOptional(address);
        Industry = NormalizeOptional(industry);
        Size = NormalizeOptional(size);
        Touch();
    }

    public void Verify()
    {
        Verified = true;
        Touch();
    }

    public void Unverify()
    {
        Verified = false;
        Touch();
    }

    private static string? NormalizeOptional(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }
}
