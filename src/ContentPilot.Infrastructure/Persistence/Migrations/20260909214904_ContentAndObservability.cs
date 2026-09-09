using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContentPilot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ContentAndObservability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "content_campaigns",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    brand_id = table.Column<Guid>(type: "uuid", nullable: false),
                    week_start = table.Column<DateOnly>(type: "date", nullable: false),
                    trigger = table.Column<short>(type: "smallint", nullable: false),
                    brand_profile_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    theme = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    budget_micro_cents = table.Column<long>(type: "bigint", nullable: false),
                    failure_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_content_campaigns", x => x.id);
                    table.ForeignKey(
                        name: "fk_content_campaigns_brands_brand_id",
                        column: x => x.brand_id,
                        principalTable: "brands",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "content_history",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    brand_id = table.Column<Guid>(type: "uuid", nullable: false),
                    content_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<short>(type: "smallint", nullable: false),
                    topic = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    pillar = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    hook = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    topic_sim_hash = table.Column<long>(type: "bigint", nullable: false),
                    hook_sim_hash = table.Column<long>(type: "bigint", nullable: false),
                    template_id = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    quality_score = table.Column<double>(type: "double precision", nullable: true),
                    human_rating = table.Column<int>(type: "integer", nullable: true),
                    approved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_content_history", x => x.id);
                    table.ForeignKey(
                        name: "fk_content_history_brands_brand_id",
                        column: x => x.brand_id,
                        principalTable: "brands",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "prompt_versions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    prompt_id = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    body = table.Column<string>(type: "text", nullable: false),
                    content_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    model_profile = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    schema_name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    registered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_prompt_versions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "agent_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: false),
                    content_item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    agent_name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    agent_version = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    prompt_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    model_id = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    attempt = table.Column<int>(type: "integer", nullable: false),
                    outcome = table.Column<short>(type: "smallint", nullable: false),
                    input_tokens = table.Column<long>(type: "bigint", nullable: false),
                    output_tokens = table.Column<long>(type: "bigint", nullable: false),
                    cache_read_tokens = table.Column<long>(type: "bigint", nullable: false),
                    cache_write_tokens = table.Column<long>(type: "bigint", nullable: false),
                    cost_micro_cents = table.Column<long>(type: "bigint", nullable: false),
                    duration_ms = table.Column<int>(type: "integer", nullable: false),
                    input_ref = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    output_ref = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    validation_failure = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    trace_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    span_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agent_runs", x => x.id);
                    table.ForeignKey(
                        name: "fk_agent_runs_content_campaigns_campaign_id",
                        column: x => x.campaign_id,
                        principalTable: "content_campaigns",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "content_items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<short>(type: "smallint", nullable: false),
                    topic = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    pillar = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    objective = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    publish_day = table.Column<int>(type: "integer", nullable: false),
                    ordinal = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    quality_attempts = table.Column<int>(type: "integer", nullable: false),
                    steps_executed = table.Column<int>(type: "integer", nullable: false),
                    best_asset_id = table.Column<Guid>(type: "uuid", nullable: true),
                    repetition_risk = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    failure_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_content_items", x => x.id);
                    table.ForeignKey(
                        name: "fk_content_items_content_campaigns_campaign_id",
                        column: x => x.campaign_id,
                        principalTable: "content_campaigns",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "cost_entries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: false),
                    content_item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    agent_run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    kind = table.Column<short>(type: "smallint", nullable: false),
                    provider = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    resource = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    units = table.Column<long>(type: "bigint", nullable: false),
                    unit_price_micro_cents_per_million = table.Column<long>(type: "bigint", nullable: false),
                    amount_micro_cents = table.Column<long>(type: "bigint", nullable: false),
                    incurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cost_entries", x => x.id);
                    table.ForeignKey(
                        name: "fk_cost_entries_content_campaigns_campaign_id",
                        column: x => x.campaign_id,
                        principalTable: "content_campaigns",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_agent_runs_agent_name_prompt_version_id_outcome",
                table: "agent_runs",
                columns: new[] { "agent_name", "prompt_version_id", "outcome" });

            migrationBuilder.CreateIndex(
                name: "ix_agent_runs_campaign_id_started_at",
                table: "agent_runs",
                columns: new[] { "campaign_id", "started_at" });

            migrationBuilder.CreateIndex(
                name: "ix_content_campaigns_brand_id_week_start",
                table: "content_campaigns",
                columns: new[] { "brand_id", "week_start" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_content_campaigns_tenant_id_status",
                table: "content_campaigns",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_content_history_brand_id_approved_at",
                table: "content_history",
                columns: new[] { "brand_id", "approved_at" });

            migrationBuilder.CreateIndex(
                name: "ix_content_items_campaign_id_ordinal",
                table: "content_items",
                columns: new[] { "campaign_id", "ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_content_items_tenant_id_status",
                table: "content_items",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_cost_entries_campaign_id",
                table: "cost_entries",
                column: "campaign_id");

            migrationBuilder.CreateIndex(
                name: "ix_cost_entries_tenant_id_incurred_at",
                table: "cost_entries",
                columns: new[] { "tenant_id", "incurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_prompt_versions_prompt_id_version",
                table: "prompt_versions",
                columns: new[] { "prompt_id", "version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_runs");

            migrationBuilder.DropTable(
                name: "content_history");

            migrationBuilder.DropTable(
                name: "content_items");

            migrationBuilder.DropTable(
                name: "cost_entries");

            migrationBuilder.DropTable(
                name: "prompt_versions");

            migrationBuilder.DropTable(
                name: "content_campaigns");
        }
    }
}
