using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContentPilot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class QualityReviews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "quality_reviews",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    content_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attempt = table.Column<int>(type: "integer", nullable: false),
                    gate = table.Column<short>(type: "smallint", nullable: false),
                    outcome = table.Column<short>(type: "smallint", nullable: false),
                    score = table.Column<double>(type: "double precision", nullable: false),
                    findings = table.Column<string>(type: "jsonb", nullable: false),
                    evaluated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    agent_run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    duration_ms = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_quality_reviews", x => x.id);
                    table.ForeignKey(
                        name: "fk_quality_reviews_content_items_content_item_id",
                        column: x => x.content_item_id,
                        principalTable: "content_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_quality_reviews_content_item_id_attempt_gate",
                table: "quality_reviews",
                columns: new[] { "content_item_id", "attempt", "gate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_quality_reviews_tenant_id_gate_evaluated_at",
                table: "quality_reviews",
                columns: new[] { "tenant_id", "gate", "evaluated_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "quality_reviews");
        }
    }
}
