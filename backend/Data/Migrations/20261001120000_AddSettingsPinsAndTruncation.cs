using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harness.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSettingsPinsAndTruncation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Truncated",
                table: "Messages",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "Archived",
                table: "Conversations",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "Pinned",
                table: "Conversations",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "Settings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    CustomInstructions = table.Column<string>(type: "TEXT", nullable: false),
                    ContextWindow = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxOutputTokens = table.Column<int>(type: "INTEGER", nullable: false),
                    ThinkingTokens = table.Column<int>(type: "INTEGER", nullable: false),
                    KeepAlive = table.Column<string>(type: "TEXT", nullable: false),
                    SearchResults = table.Column<int>(type: "INTEGER", nullable: false),
                    PageCharacters = table.Column<int>(type: "INTEGER", nullable: false),
                    AutoTitles = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settings", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Settings");

            migrationBuilder.DropColumn(
                name: "Truncated",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "Archived",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "Pinned",
                table: "Conversations");
        }
    }
}
