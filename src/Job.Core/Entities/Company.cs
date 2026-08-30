using SharedKernel;

namespace Job.Core.Entities;

public class Company : Entity
{
    // Column length limits — mirror JobDbContext fluent config and guard POST/PUT DTOs
    // against Npgsql 22001 (value too long) → 500 (returns 400 ValidationProblem instead).
    public const int NameMaxLength = 256;
    public const int TaxCodeMaxLength = 20;
    public const int LogoUrlMaxLength = 2048;
    public const int WebsiteMaxLength = 2048;
    public const int AddressMaxLength = 512;
    public const int IndustryMaxLength = 128;
    public const int SizeMaxLength = 64;

    public string Name { get; private set; } = "";
    public string? TaxCode { get; private set; }
    public bool Verified { get; private set; }
    public string? LogoUrl { get; private set; }
    public string? Website { get; private set; }
    public string? Description { get; private set; }
    public string? Address { get; private set; }
    public string? Industry { get; private set; }
    public string? Size { get; private set; }

    /// <summary>
    /// The Recruiter who created this company profile (set once at creation, immutable).
    /// SRS extension: Company in 3-must-have-fr.md:87 / 10-appendices.md:269 is declared with
    /// `created_by FK → users.id` to support owner-only PUT /api/companies/{id} (US-07b).
    /// </summary>
    public Guid CreatedBy { get; private set; }

    private Company() { }

    public Company(
        string name,
        Guid createdBy,
        string? taxCode = null,
        string? logoUrl = null,
        string? website = null,
        string? description = null,
        string? address = null,
        string? industry = null,
        string? size = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name, nameof(name));
        if (createdBy == Guid.Empty)
            throw new ArgumentException("CreatedBy cannot be empty.", nameof(createdBy));
        Name = name.Trim();
        CreatedBy = createdBy;
        TaxCode = NormalizeOptional(taxCode);
        LogoUrl = NormalizeOptional(logoUrl);
        Website = NormalizeOptional(website);
        Description = NormalizeOptional(description);
        Address = NormalizeOptional(address);
        Industry = NormalizeOptional(industry);
        Size = NormalizeOptional(size);
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
