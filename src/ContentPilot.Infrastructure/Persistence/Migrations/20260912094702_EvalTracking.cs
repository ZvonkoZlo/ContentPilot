using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContentPilot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EvalTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "eval_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    mode = table.Column<short>(type: "smallint", nullable: false),
                    run_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    total_scenarios = table.Column<int>(type: "integer", nullable: false),
                    passed_scenarios = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_eval_runs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "eval_results",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    eval_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scenario_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    kind = table.Column<short>(type: "smallint", nullable: false),
                    passed = table.Column<bool>(type: "boolean", nullable: false),
                    detail = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    duration_ms = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_eval_results", x => x.id);
                    table.ForeignKey(
                        name: "fk_eval_results_eval_runs_eval_run_id",
                        column: x => x.eval_run_id,
                        principalTable: "eval_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_eval_results_eval_run_id_scenario_name",
                table: "eval_results",
                columns: new[] { "eval_run_id", "scenario_name" });

            migrationBuilder.CreateIndex(
                name: "ix_eval_results_scenario_name",
                table: "eval_results",
                column: "scenario_name");

            migrationBuilder.CreateIndex(
                name: "ix_eval_runs_run_at",
                table: "eval_runs",
                column: "run_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "eval_results");

            migrationBuilder.DropTable(
                name: "eval_runs");
        }
    }
}
