using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nullbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialRuleSets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "rule_sets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    ActiveVersion = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rule_sets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "rule_set_versions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RuleSetId = table.Column<int>(type: "INTEGER", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    Json = table.Column<string>(type: "TEXT", nullable: false),
                    Hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rule_set_versions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_rule_set_versions_rule_sets_RuleSetId",
                        column: x => x.RuleSetId,
                        principalTable: "rule_sets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_rule_set_versions_RuleSetId_Version",
                table: "rule_set_versions",
                columns: new[] { "RuleSetId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_rule_sets_Name",
                table: "rule_sets",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "rule_set_versions");

            migrationBuilder.DropTable(
                name: "rule_sets");
        }
    }
}
