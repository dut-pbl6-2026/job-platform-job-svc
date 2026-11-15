using Job.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Job.Infrastructure.Companies;

/// <summary>Which company identity value is already taken.</summary>
public enum CompanyConflict
{
    None,
    Name,
    TaxCode,
    Domain,
}

/// <summary>
/// Uniqueness rules shared by availability lookups, internal company creation and the
/// database constraint mapping (SRS AUTH-01-10). Name is case-insensitive; tax code is compared
/// case-insensitively after trimming, as in the public create rules.
/// </summary>
public static class CompanyUniqueness
{
    /// <summary>Postgres unique-violation SQLSTATE.</summary>
    public const string UniqueViolationState = "23505";

    /// <summary>Constraint names of the unique indexes (EF default names, see JobDbContext).</summary>
    public const string NameIndex = "IX_Companies_Name_Lower";
    public const string TaxCodeIndex = "IX_Companies_TaxCode";
    public const string DomainIndex = "IX_Companies_EmailDomain";

    public static async Task<CompanyConflict> FindConflictAsync(
        JobDbContext db,
        string name,
        string? taxCode,
        string? emailDomain,
        CancellationToken ct)
    {
        var lowerName = name.Trim().ToLower();
        if (await db.Companies.AnyAsync(c => c.Name.ToLower() == lowerName, ct))
            return CompanyConflict.Name;

        var lowerTax = taxCode?.Trim().ToLower();
        if (!string.IsNullOrEmpty(lowerTax) &&
            await db.Companies.AnyAsync(c => c.TaxCode != null && c.TaxCode.ToLower() == lowerTax, ct))
            return CompanyConflict.TaxCode;

        if (!string.IsNullOrEmpty(emailDomain) &&
            await db.Companies.AnyAsync(c => c.EmailDomain == emailDomain, ct))
            return CompanyConflict.Domain;

        return CompanyConflict.None;
    }

    /// <summary>Maps a unique-violation to the conflict it names, or None when it is another constraint.</summary>
    public static CompanyConflict FromException(Exception ex)
    {
        if (ex is DbUpdateException { InnerException: PostgresException pg } &&
            pg.SqlState == UniqueViolationState)
        {
            return pg.ConstraintName switch
            {
                NameIndex => CompanyConflict.Name,
                TaxCodeIndex => CompanyConflict.TaxCode,
                DomainIndex => CompanyConflict.Domain,
                _ => CompanyConflict.None,
            };
        }
        return CompanyConflict.None;
    }
}
