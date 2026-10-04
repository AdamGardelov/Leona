using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harness.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddReadState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ReadAt",
                table: "Conversations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RepliedAt",
                table: "Conversations",
                type: "TEXT",
                nullable: true);

            // Existing chats start out read.
            migrationBuilder.Sql("UPDATE Conversations SET RepliedAt = UpdatedAt, ReadAt = UpdatedAt;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReadAt",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "RepliedAt",
                table: "Conversations");
        }
    }
}
