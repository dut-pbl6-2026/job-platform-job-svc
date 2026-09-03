using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Job.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropSavedJobsSavedAt : Migration
    {
        /// <summary>
        /// Removes the redundant SavedJobs.SavedAt column. Per AGENTS.md design, SRS 3.3.4
        /// `saved_at` is surfaced via SavedJob.SavedAt => Entity.CreatedAt — no extra column.
        /// InitialCreate added both SavedAt and CreatedAt; this dedicated migration (kept
        /// separate from AddCompanyCreatedBy for traceability; fresh job_platform_job DB,
        /// no rows) drops the duplicate.
        /// </summary>
        /// <param name="migrationBuilder"></param>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SavedAt",
                table: "SavedJobs");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "SavedAt",
                table: "SavedJobs",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));
        }
    }
}
