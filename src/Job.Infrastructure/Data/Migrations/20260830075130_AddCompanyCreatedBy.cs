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
            // GUARD (release checklist): fail fast instead of silently creating
            // un-ownable rows. If any row exists, the DBA must backfill first:
            //   1. Change nullable: false → nullable: true, remove defaultValue
            //   2. Backfill: UPDATE "Companies" SET "CreatedBy" = <real-recruiter-guid>
            //   3. Set NOT NULL in a follow-up migration
            //
            // Rows with CreatedBy == Guid.Empty are safe but cannot be updated via
            // PUT /api/companies/{id} (ownership check rejects them) — intentional.
            // NOTE: editing this file is safe for DBs where the migration already ran —
            // EF never re-executes applied migrations; the guard only fires on fresh runs.
            migrationBuilder.Sql(
                "DO $$ BEGIN IF EXISTS (SELECT 1 FROM \"Companies\") THEN " +
                "RAISE EXCEPTION 'Migration AddCompanyCreatedBy requires an empty Companies table. " +
                "Backfill CreatedBy for existing rows before migrating (see migration comment).'; " +
                "END IF; END $$;");

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
