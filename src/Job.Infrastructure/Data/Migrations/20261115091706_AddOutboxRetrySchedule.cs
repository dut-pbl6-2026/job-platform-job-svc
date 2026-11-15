using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Job.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOutboxRetrySchedule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "NextAttemptAt",
                table: "OutboxEvents",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.CreateIndex(
                name: "IX_OutboxEvents_AggregateId_CreatedAt",
                table: "OutboxEvents",
                columns: new[] { "AggregateId", "CreatedAt" },
                filter: "NOT \"IsPublished\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OutboxEvents_AggregateId_CreatedAt",
                table: "OutboxEvents");

            migrationBuilder.DropColumn(
                name: "NextAttemptAt",
                table: "OutboxEvents");
        }
    }
}
