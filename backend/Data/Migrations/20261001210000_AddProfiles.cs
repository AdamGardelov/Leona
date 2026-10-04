using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harness.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Profiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Owner = table.Column<bool>(type: "INTEGER", nullable: false),
                    CustomInstructions = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Profiles", x => x.Id);
                });

            // Everything that exists today belongs to the computer owner's profile, including the custom
            // instructions that used to be shared.
            migrationBuilder.Sql(
                "INSERT INTO Profiles (Id, Name, Owner, CustomInstructions, CreatedAt) " +
                "VALUES (1, 'Adam', 1, COALESCE((SELECT CustomInstructions FROM Settings WHERE Id = 1), ''), " +
                "strftime('%Y-%m-%d %H:%M:%f', 'now'));");

            migrationBuilder.DropColumn(
                name: "CustomInstructions",
                table: "Settings");

            migrationBuilder.AddColumn<int>(
                name: "ProfileId",
                table: "Watches",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "ProfileId",
                table: "Uploads",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "ProfileId",
                table: "ScheduledTasks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "ProfileId",
                table: "PushSubscriptions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "ProfileId",
                table: "Notifications",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "ProfileId",
                table: "Memories",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "ProfileId",
                table: "DeviceSessions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "ProfileId",
                table: "Conversations",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "ProfileId",
                table: "Accounts",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);


            migrationBuilder.CreateIndex(
                name: "IX_Watches_ProfileId",
                table: "Watches",
                column: "ProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_Uploads_ProfileId",
                table: "Uploads",
                column: "ProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledTasks_ProfileId",
                table: "ScheduledTasks",
                column: "ProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_PushSubscriptions_ProfileId",
                table: "PushSubscriptions",
                column: "ProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_ProfileId",
                table: "Notifications",
                column: "ProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_Memories_ProfileId",
                table: "Memories",
                column: "ProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceSessions_ProfileId",
                table: "DeviceSessions",
                column: "ProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_ProfileId",
                table: "Conversations",
                column: "ProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_ProfileId",
                table: "Accounts",
                column: "ProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_Profiles_Name",
                table: "Profiles",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Profiles");

            migrationBuilder.DropIndex(
                name: "IX_Watches_ProfileId",
                table: "Watches");

            migrationBuilder.DropIndex(
                name: "IX_Uploads_ProfileId",
                table: "Uploads");

            migrationBuilder.DropIndex(
                name: "IX_ScheduledTasks_ProfileId",
                table: "ScheduledTasks");

            migrationBuilder.DropIndex(
                name: "IX_PushSubscriptions_ProfileId",
                table: "PushSubscriptions");

            migrationBuilder.DropIndex(
                name: "IX_Notifications_ProfileId",
                table: "Notifications");

            migrationBuilder.DropIndex(
                name: "IX_Memories_ProfileId",
                table: "Memories");

            migrationBuilder.DropIndex(
                name: "IX_DeviceSessions_ProfileId",
                table: "DeviceSessions");

            migrationBuilder.DropIndex(
                name: "IX_Conversations_ProfileId",
                table: "Conversations");

            migrationBuilder.DropIndex(
                name: "IX_Accounts_ProfileId",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "ProfileId",
                table: "Watches");

            migrationBuilder.DropColumn(
                name: "ProfileId",
                table: "Uploads");

            migrationBuilder.DropColumn(
                name: "ProfileId",
                table: "ScheduledTasks");

            migrationBuilder.DropColumn(
                name: "ProfileId",
                table: "PushSubscriptions");

            migrationBuilder.DropColumn(
                name: "ProfileId",
                table: "Notifications");

            migrationBuilder.DropColumn(
                name: "ProfileId",
                table: "Memories");

            migrationBuilder.DropColumn(
                name: "ProfileId",
                table: "DeviceSessions");

            migrationBuilder.DropColumn(
                name: "ProfileId",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "ProfileId",
                table: "Accounts");

            migrationBuilder.AddColumn<string>(
                name: "CustomInstructions",
                table: "Settings",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }
    }
}
