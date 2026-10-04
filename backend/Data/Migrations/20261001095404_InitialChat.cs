using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harness.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialChat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Adopt the original EnsureCreated schema without deleting existing conversations.
            migrationBuilder.Sql("""
                CREATE TABLE IF NOT EXISTS "Conversations" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_Conversations" PRIMARY KEY AUTOINCREMENT,
                    "Title" TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS "Messages" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_Messages" PRIMARY KEY AUTOINCREMENT,
                    "ConversationId" INTEGER NOT NULL,
                    "Role" TEXT NOT NULL,
                    "Content" TEXT NOT NULL,
                    "Thinking" TEXT NOT NULL,
                    "Complete" INTEGER NOT NULL,
                    CONSTRAINT "FK_Messages_Conversations_ConversationId" FOREIGN KEY ("ConversationId") REFERENCES "Conversations" ("Id") ON DELETE CASCADE);
                CREATE INDEX IF NOT EXISTS "IX_Messages_ConversationId" ON "Messages" ("ConversationId");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Messages");

            migrationBuilder.DropTable(
                name: "Conversations");
        }
    }
}
