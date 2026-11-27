using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nullbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class CleanupRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "cleanup_runs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ConfigId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    DryRun = table.Column<bool>(type: "INTEGER", nullable: false),
                    SampleSize = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FinishedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Label = table.Column<string>(type: "TEXT", maxLength: 225, nullable: true),
                    Matched = table.Column<int>(type: "INTEGER", nullable: false),
                    Changed = table.Column<int>(type: "INTEGER", nullable: false),
                    RulesJson = table.Column<string>(type: "TEXT", nullable: false),
                    Error = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cleanup_runs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_cleanup_runs_CreatedUtc",
                table: "cleanup_runs",
                column: "CreatedUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cleanup_runs");
        }
    }
}
