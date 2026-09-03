using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Job.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReplaceCompanyNameIndexWithFunctionalLower : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Drop the old case-sensitive plain-text unique index on "Name".
            // Replace it with a functional unique index on lower("Name") so that
            // Postgres enforces the same case-insensitive uniqueness that the application
            // checks via .ToLower() == .ToLower().
            // Without this, two concurrent POST /api/companies requests with names that
            // differ only in case (e.g. "Acme" vs "acme") would both pass the AnyAsync
            // pre-check and the second would hit IX_Companies_Name without being caught.
            migrationBuilder.Sql(
                "DROP INDEX IF EXISTS \"IX_Companies_Name\";");

            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX \"IX_Companies_Name_Lower\" ON \"Companies\" (lower(\"Name\"));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DROP INDEX IF EXISTS \"IX_Companies_Name_Lower\";");

            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX \"IX_Companies_Name\" ON \"Companies\" (\"Name\");");
        }
    }
}
