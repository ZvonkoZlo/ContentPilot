using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContentPilot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Workflow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "budget_reservations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: false),
                    content_item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    estimate_micro_cents = table.Column<long>(type: "bigint", nullable: false),
                    reserved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    released_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_budget_reservations", x => x.id);
                    table.ForeignKey(
                        name: "fk_budget_reservations_content_campaigns_campaign_id",
                        column: x => x.campaign_id,
                        principalTable: "content_campaigns",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "content_revisions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    content_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attempt = table.Column<int>(type: "integer", nullable: false),
                    reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    remediation_action = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    findings_json = table.Column<string>(type: "jsonb", nullable: true),
                    restart_at_step = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_content_revisions", x => x.id);
                    table.ForeignKey(
                        name: "fk_content_revisions_content_items_content_item_id",
                        column: x => x.content_item_id,
                        principalTable: "content_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "workflow_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope = table.Column<short>(type: "smallint", nullable: false),
                    entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    state = table.Column<short>(type: "smallint", nullable: false),
                    schema_repair_attempts = table.Column<int>(type: "integer", nullable: false),
                    transient_attempts = table.Column<int>(type: "integer", nullable: false),
                    quality_attempts = table.Column<int>(type: "integer", nullable: false),
                    max_schema_repair_attempts = table.Column<int>(type: "integer", nullable: false),
                    max_transient_attempts = table.Column<int>(type: "integer", nullable: false),
                    max_quality_attempts = table.Column<int>(type: "integer", nullable: false),
                    steps_executed = table.Column<int>(type: "integer", nullable: false),
                    max_steps_executed = table.Column<int>(type: "integer", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deadline = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    lease_owner = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    last_error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workflow_runs", x => x.id);
                    table.ForeignKey(
                        name: "fk_workflow_runs_content_campaigns_campaign_id",
                        column: x => x.campaign_id,
                        principalTable: "content_campaigns",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "workflow_steps",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workflow_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    step_name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    attempt = table.Column<int>(type: "integer", nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    outcome = table.Column<short>(type: "smallint", nullable: false),
                    agent_run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workflow_steps", x => x.id);
                    table.ForeignKey(
                        name: "fk_workflow_steps_workflow_runs_workflow_run_id",
                        column: x => x.workflow_run_id,
                        principalTable: "workflow_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_budget_reservations_campaign_id_released_at_expires_at",
                table: "budget_reservations",
                columns: new[] { "campaign_id", "released_at", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "ix_budget_reservations_content_item_id_released_at_expires_at",
                table: "budget_reservations",
                columns: new[] { "content_item_id", "released_at", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "ix_content_revisions_content_item_id_attempt",
                table: "content_revisions",
                columns: new[] { "content_item_id", "attempt" });

            migrationBuilder.CreateIndex(
                name: "ix_workflow_runs_campaign_id",
                table: "workflow_runs",
                column: "campaign_id");

            migrationBuilder.CreateIndex(
                name: "ix_workflow_runs_scope_entity_id",
                table: "workflow_runs",
                columns: new[] { "scope", "entity_id" },
                unique: true,
                filter: "state = 0");

            migrationBuilder.CreateIndex(
                name: "ix_workflow_runs_state_lease_until",
                table: "workflow_runs",
                columns: new[] { "state", "lease_until" });

            migrationBuilder.CreateIndex(
                name: "ix_workflow_runs_tenant_id_campaign_id",
                table: "workflow_runs",
                columns: new[] { "tenant_id", "campaign_id" });

            migrationBuilder.CreateIndex(
                name: "ix_workflow_steps_idempotency_key",
                table: "workflow_steps",
                column: "idempotency_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_workflow_steps_workflow_run_id_started_at",
                table: "workflow_steps",
                columns: new[] { "workflow_run_id", "started_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "budget_reservations");

            migrationBuilder.DropTable(
                name: "content_revisions");

            migrationBuilder.DropTable(
                name: "workflow_steps");

            migrationBuilder.DropTable(
                name: "workflow_runs");
        }
    }
}
