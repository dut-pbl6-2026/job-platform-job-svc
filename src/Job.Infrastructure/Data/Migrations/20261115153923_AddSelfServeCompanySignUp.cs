using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Job.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSelfServeCompanySignUp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Email",
                table: "InviteCodes",
                type: "character varying(320)",
                maxLength: 320,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "RevokeReason",
                table: "InviteCodes",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RevokedAt",
                table: "InviteCodes",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "InviteCodes",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Active");

            migrationBuilder.AddColumn<string>(
                name: "EmailDomain",
                table: "Companies",
                type: "character varying(253)",
                maxLength: 253,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CompanyJoinRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DecidedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DecisionReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CompanyJoinRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CompanyJoinRequests_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Companies_EmailDomain",
                table: "Companies",
                column: "EmailDomain",
                unique: true,
                filter: "\"EmailDomain\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CompanyJoinRequests_CompanyId",
                table: "CompanyJoinRequests",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_CompanyJoinRequests_ExpiresAt",
                table: "CompanyJoinRequests",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_CompanyJoinRequests_UserId",
                table: "CompanyJoinRequests",
                column: "UserId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CompanyJoinRequests");

            migrationBuilder.DropIndex(
                name: "IX_Companies_EmailDomain",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "Email",
                table: "InviteCodes");

            migrationBuilder.DropColumn(
                name: "RevokeReason",
                table: "InviteCodes");

            migrationBuilder.DropColumn(
                name: "RevokedAt",
                table: "InviteCodes");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "InviteCodes");

            migrationBuilder.DropColumn(
                name: "EmailDomain",
                table: "Companies");
        }
    }
}
