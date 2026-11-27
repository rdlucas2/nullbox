using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nullbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class PipelineDecisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "app_settings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Value = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_app_settings", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "decisions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MessageId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    RuleSetKey = table.Column<string>(type: "TEXT", maxLength: 96, nullable: false),
                    Mode = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Outcome = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    DecisionsJson = table.Column<string>(type: "TEXT", nullable: false),
                    ActionsJson = table.Column<string>(type: "TEXT", nullable: false),
                    SkippedJson = table.Column<string>(type: "TEXT", nullable: false),
                    Model = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    AnswersJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_decisions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "message_failures",
                columns: table => new
                {
                    MessageId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Poison = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_message_failures", x => x.MessageId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_decisions_CreatedUtc",
                table: "decisions",
                column: "CreatedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_decisions_MessageId_RuleSetKey",
                table: "decisions",
                columns: new[] { "MessageId", "RuleSetKey" });

            migrationBuilder.CreateIndex(
                name: "IX_decisions_Outcome_CreatedUtc",
                table: "decisions",
                columns: new[] { "Outcome", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_message_failures_Poison_UpdatedUtc",
                table: "message_failures",
                columns: new[] { "Poison", "UpdatedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "app_settings");

            migrationBuilder.DropTable(
                name: "decisions");

            migrationBuilder.DropTable(
                name: "message_failures");
        }
    }
}
