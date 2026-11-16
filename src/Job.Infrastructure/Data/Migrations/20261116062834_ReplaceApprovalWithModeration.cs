using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Job.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReplaceApprovalWithModeration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Moderation replaces the admin approve/reject flow: jobs publish instantly.
            // Convert legacy approval-queue rows so no job stays invisible:
            //   Pending  -> Active  (it was only waiting for an approval that no longer exists)
            //   Rejected -> Closed  (never publish rejected content; keep it out of every public list)
            // The rejection audit columns are renamed below to the moderation columns, so a legacy
            // rejected row keeps its reason/actor/time on a Closed row (informational, not restorable).
            migrationBuilder.Sql("UPDATE \"Jobs\" SET \"Status\" = 'Active' WHERE \"Status\" = 'Pending';");
            migrationBuilder.Sql("UPDATE \"Jobs\" SET \"Status\" = 'Closed' WHERE \"Status\" = 'Rejected';");

            migrationBuilder.DropColumn(
                name: "ApprovedAt",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "ApprovedBy",
                table: "Jobs");

            migrationBuilder.RenameColumn(
                name: "RejectionReason",
                table: "Jobs",
                newName: "ModerationReason");

            migrationBuilder.RenameColumn(
                name: "RejectedBy",
                table: "Jobs",
                newName: "ModeratedBy");

            migrationBuilder.RenameColumn(
                name: "RejectedAt",
                table: "Jobs",
                newName: "ModeratedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Status conversion is intentionally not reversed: Closed rows may have been Closed before
            // this migration, so mapping them back to Rejected would invent approval states.
            migrationBuilder.RenameColumn(
                name: "ModerationReason",
                table: "Jobs",
                newName: "RejectionReason");

            migrationBuilder.RenameColumn(
                name: "ModeratedBy",
                table: "Jobs",
                newName: "RejectedBy");

            migrationBuilder.RenameColumn(
                name: "ModeratedAt",
                table: "Jobs",
                newName: "RejectedAt");

            migrationBuilder.AddColumn<DateTime>(
                name: "ApprovedAt",
                table: "Jobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ApprovedBy",
                table: "Jobs",
                type: "uuid",
                nullable: true);
        }
    }
}
