using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nullbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class DecisionAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "Confidence",
                table: "decisions",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Error",
                table: "decisions",
                type: "TEXT",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InputsHash",
                table: "decisions",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LabelsJson",
                table: "decisions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RuleSetName",
                table: "decisions",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "RuleSetVersion",
                table: "decisions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "StateJson",
                table: "decisions",
                type: "TEXT",
                nullable: true);

            // Rows written before this migration: split "name@vN" into the new columns.
            migrationBuilder.Sql(
                """
                UPDATE decisions
                SET RuleSetName = substr(RuleSetKey, 1, instr(RuleSetKey, '@v') - 1),
                    RuleSetVersion = CAST(substr(RuleSetKey, instr(RuleSetKey, '@v') + 2) AS INTEGER)
                WHERE RuleSetName = '' AND instr(RuleSetKey, '@v') > 1;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_decisions_RuleSetName_RuleSetVersion",
                table: "decisions",
                columns: new[] { "RuleSetName", "RuleSetVersion" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_decisions_RuleSetName_RuleSetVersion",
                table: "decisions");

            migrationBuilder.DropColumn(
                name: "Confidence",
                table: "decisions");

            migrationBuilder.DropColumn(
                name: "Error",
                table: "decisions");

            migrationBuilder.DropColumn(
                name: "InputsHash",
                table: "decisions");

            migrationBuilder.DropColumn(
                name: "LabelsJson",
                table: "decisions");

            migrationBuilder.DropColumn(
                name: "RuleSetName",
                table: "decisions");

            migrationBuilder.DropColumn(
                name: "RuleSetVersion",
                table: "decisions");

            migrationBuilder.DropColumn(
                name: "StateJson",
                table: "decisions");
        }
    }
}
