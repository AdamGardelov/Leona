using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harness.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddConversationTimes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAt",
                table: "Conversations",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAt",
                table: "Conversations",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            // Existing conversations take their times from their runs; any without runs count as now.
            migrationBuilder.Sql(
                "UPDATE Conversations SET " +
                "CreatedAt = COALESCE((SELECT MIN(r.CreatedAt) FROM Runs r WHERE r.ConversationId = Conversations.Id), strftime('%Y-%m-%d %H:%M:%f', 'now')), " +
                "UpdatedAt = COALESCE((SELECT MAX(r.CreatedAt) FROM Runs r WHERE r.ConversationId = Conversations.Id), strftime('%Y-%m-%d %H:%M:%f', 'now'));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "Conversations");
        }
    }
}
