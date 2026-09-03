using Job.Core.Entities;
using JobPosting = Job.Core.Entities.Job;

namespace Job.Tests;

public class JobEntityTests
{
    [Fact]
    public void SoftDeleteMarksJobAsDeleted()
    {
        var job = NewJob();

        job.SoftDelete();

        Assert.Equal(JobStatus.Deleted, job.Status);
    }

    [Fact]
    public void IncrementViewIncreasesViewCount()
    {
        var job = NewJob();

        job.IncrementView();
        job.IncrementView();

        Assert.Equal(2, job.ViewCount);
    }

    [Fact]
    public void CloseMarksJobAsClosed()
    {
        var job = NewJob();
        job.Close();
        Assert.Equal(JobStatus.Closed, job.Status);
    }

    [Fact]
    public void ReopenMarksJobAsActive()
    {
        var job = NewJob();
        job.Close();
        job.Reopen();
        Assert.Equal(JobStatus.Active, job.Status);
    }

    // M3 — salary range validation
    [Fact]
    public void Ctor_ThrowsWhenSalaryMinNegative()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new JobPosting(
            "Dev", "Desc", Guid.NewGuid(), "Da Nang", Guid.NewGuid(),
            salaryMin: -1));
    }

    [Fact]
    public void Ctor_ThrowsWhenSalaryMaxNegative()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new JobPosting(
            "Dev", "Desc", Guid.NewGuid(), "Da Nang", Guid.NewGuid(),
            salaryMax: -1));
    }

    [Fact]
    public void Ctor_ThrowsWhenSalaryMinGreaterThanMax()
    {
        Assert.Throws<ArgumentException>(() => new JobPosting(
            "Dev", "Desc", Guid.NewGuid(), "Da Nang", Guid.NewGuid(),
            salaryMin: 100, salaryMax: 10));
    }

    [Fact]
    public void Ctor_AcceptsValidSalaryRange()
    {
        var job = new JobPosting(
            "Dev", "Desc", Guid.NewGuid(), "Da Nang", Guid.NewGuid(),
            salaryMin: 10_000_000, salaryMax: 30_000_000);
        Assert.Equal(10_000_000, job.SalaryMin);
        Assert.Equal(30_000_000, job.SalaryMax);
    }

    // M1 — null guards
    [Fact]
    public void Ctor_ThrowsWhenTitleEmpty()
    {
        Assert.Throws<ArgumentException>(() => new JobPosting(
            "", "Desc", Guid.NewGuid(), "Da Nang", Guid.NewGuid()));
    }

    [Fact]
    public void Ctor_ThrowsWhenLocationWhitespace()
    {
        Assert.Throws<ArgumentException>(() => new JobPosting(
            "Dev", "Desc", Guid.NewGuid(), "   ", Guid.NewGuid()));
    }

    // M2 — NormalizeCurrency null/whitespace
    [Fact]
    public void Ctor_DefaultsCurrencyToVndWhenEmpty()
    {
        var job = new JobPosting(
            "Dev", "Desc", Guid.NewGuid(), "Da Nang", Guid.NewGuid(),
            salaryCurrency: "");
        Assert.Equal("VND", job.SalaryCurrency);
    }

    [Fact]
    public void Ctor_NormalizesCurrencyToUppercase()
    {
        var job = new JobPosting(
            "Dev", "Desc", Guid.NewGuid(), "Da Nang", Guid.NewGuid(),
            salaryCurrency: "usd");
        Assert.Equal("USD", job.SalaryCurrency);
    }

    private static JobPosting NewJob() =>
        new("Backend Developer", "Build and maintain services", Guid.NewGuid(), "Da Nang", Guid.NewGuid());
}

public class CategoryEntityTests
{
    [Fact]
    public void Ctor_ThrowsWhenNameEmpty()
    {
        Assert.Throws<ArgumentException>(() => new Category(""));
    }

    [Fact]
    public void Ctor_TrimsName()
    {
        var cat = new Category("  IT  ");
        Assert.Equal("IT", cat.Name);
    }

    [Fact]
    public void Update_ThrowsWhenNameWhitespace()
    {
        var cat = new Category("IT");
        Assert.Throws<ArgumentException>(() => cat.Update("  ", null));
    }
}

public class CompanyEntityTests
{
    private readonly Guid _owner = Guid.NewGuid();

    [Fact]
    public void Ctor_ThrowsWhenNameEmpty()
    {
        Assert.Throws<ArgumentException>(() => new Company("", _owner));
    }

    [Fact]
    public void Ctor_ThrowsWhenCreatedByEmpty()
    {
        Assert.Throws<ArgumentException>(() => new Company("Corp", Guid.Empty));
    }

    [Fact]
    public void Ctor_TrimsName()
    {
        var company = new Company("  Acme Corp  ", _owner);
        Assert.Equal("Acme Corp", company.Name);
    }

    [Fact]
    public void Ctor_SetsCreatedBy()
    {
        var company = new Company("Acme Corp", _owner);
        Assert.Equal(_owner, company.CreatedBy);
    }

    [Fact]
    public void Ctor_SetsAllOptionalFields()
    {
        var company = new Company(
            "Acme Corp",
            _owner,
            taxCode: "TAX-123",
            logoUrl: "https://logo.png",
            website: "https://acme.com",
            description: "A description",
            address: "123 Street",
            industry: "Technology",
            size: "50-200"
        );

        Assert.Equal("Acme Corp", company.Name);
        Assert.Equal(_owner, company.CreatedBy);
        Assert.Equal("TAX-123", company.TaxCode);
        Assert.Equal("https://logo.png", company.LogoUrl);
        Assert.Equal("https://acme.com", company.Website);
        Assert.Equal("A description", company.Description);
        Assert.Equal("123 Street", company.Address);
        Assert.Equal("Technology", company.Industry);
        Assert.Equal("50-200", company.Size);
        Assert.False(company.Verified);
    }

    [Fact]
    public void Update_ThrowsWhenNameWhitespace()
    {
        var company = new Company("Acme Corp", _owner);
        Assert.Throws<ArgumentException>(() =>
            company.Update("  ", null, null, null, null, null, null, null));
    }
}
