using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Job.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCompanyCreatedBy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ASSERTION: All 6 pbl6-* PostgreSQL environments (job_platform_job DB) have an
            // empty "Companies" table at the time this migration runs — verified before merge.
            // Therefore the Guid.Empty default is inert: it only applies to rows inserted
            // before this migration, and there are none.
            //
            // If, in a future environment, rows exist before this migration:
            //   1. Change nullable: false → nullable: true, remove defaultValue
            //   2. Backfill: UPDATE "Companies" SET "CreatedBy" = <real-recruiter-guid>
            //   3. Set NOT NULL in a follow-up migration
            //
            // Rows with CreatedBy == Guid.Empty are safe but cannot be updated via
            // PUT /api/companies/{id} (ownership check rejects them) — intentional.
            migrationBuilder.AddColumn<Guid>(
                name: "CreatedBy",
                table: "Companies",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateIndex(
                name: "IX_Companies_CreatedBy",
                table: "Companies",
                column: "CreatedBy");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Companies_CreatedBy",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "CreatedBy",
                table: "Companies");
        }
    }
}
