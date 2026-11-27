using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nullbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class SavedRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "saved_rules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false),
                    Json = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_saved_rules", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_saved_rules_Name",
                table: "saved_rules",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_saved_rules_Position",
                table: "saved_rules",
                column: "Position");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "saved_rules");
        }
    }
}
