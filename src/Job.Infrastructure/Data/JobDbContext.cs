using Job.Core.Entities;
using Microsoft.EntityFrameworkCore;
using JobPosting = Job.Core.Entities.Job;

namespace Job.Infrastructure.Data;

public class JobDbContext : DbContext
{
    public JobDbContext(DbContextOptions<JobDbContext> options) : base(options) { }

    public DbSet<JobPosting> Jobs => Set<JobPosting>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Company> Companies => Set<Company>();
    public DbSet<SavedJob> SavedJobs => Set<SavedJob>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<Category>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.Name).HasMaxLength(128).IsRequired();
        });

        b.Entity<Company>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Name).IsUnique();
            e.HasIndex(x => x.TaxCode).IsUnique().HasFilter("\"TaxCode\" IS NOT NULL");
            e.Property(x => x.Name).HasMaxLength(Company.NameMaxLength).IsRequired();
            e.Property(x => x.TaxCode).HasMaxLength(Company.TaxCodeMaxLength);
            e.Property(x => x.Verified).HasDefaultValue(false);
            e.Property(x => x.LogoUrl).HasMaxLength(Company.LogoUrlMaxLength);
            e.Property(x => x.Website).HasMaxLength(Company.WebsiteMaxLength);
            e.Property(x => x.Address).HasMaxLength(Company.AddressMaxLength);
            e.Property(x => x.Industry).HasMaxLength(Company.IndustryMaxLength);
            e.Property(x => x.Size).HasMaxLength(Company.SizeMaxLength);
            // Ownership tracking — set at creation, used for PUT authorization
            e.Property(x => x.CreatedBy).IsRequired();
            e.HasIndex(x => x.CreatedBy);
        });

        b.Entity<JobPosting>(e =>
        {
            e.HasKey(x => x.Id);

            // M6: use enum stored as string (readable in DB), max 32 chars
            e.Property(x => x.Status)
                .HasConversion<string>()
                .HasMaxLength(32)
                .HasDefaultValue(JobStatus.Active);

            // A2: global query filter — hide Deleted jobs by default (JOB-01-05)
            e.HasQueryFilter(x => x.Status != JobStatus.Deleted);

            e.Property(x => x.Title).HasMaxLength(256).IsRequired();
            e.Property(x => x.Description).IsRequired();
            e.Property(x => x.Location).HasMaxLength(256);
            e.Property(x => x.SalaryMin).HasPrecision(18, 2);
            e.Property(x => x.SalaryMax).HasPrecision(18, 2);
            e.Property(x => x.SalaryCurrency).HasMaxLength(3).HasDefaultValue("VND");
            e.Property(x => x.EmploymentType).HasMaxLength(64).HasDefaultValue("FullTime");
            e.Property(x => x.ExperienceLevel).HasMaxLength(64).HasDefaultValue("Entry");
            e.Property(x => x.ViewCount).HasDefaultValue(0);
            e.HasOne(x => x.Company)
                .WithMany()
                .HasForeignKey(x => x.CompanyId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Category)
                .WithMany()
                .HasForeignKey(x => x.CategoryId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => x.RecruiterId);
            e.HasIndex(x => x.Status);
        });

        b.Entity<SavedJob>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.UserId, x.JobId }).IsUnique();
            e.HasOne(x => x.Job)
                .WithMany()
                .HasForeignKey(x => x.JobId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
