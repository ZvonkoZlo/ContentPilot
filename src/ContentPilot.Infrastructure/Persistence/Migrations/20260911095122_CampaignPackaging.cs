using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContentPilot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CampaignPackaging : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "campaign_packages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: false),
                    manifest_json = table.Column<string>(type: "jsonb", nullable: false),
                    zip_key = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    built_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    email_sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_campaign_packages", x => x.id);
                    table.ForeignKey(
                        name: "fk_campaign_packages_content_campaigns_campaign_id",
                        column: x => x.campaign_id,
                        principalTable: "content_campaigns",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "human_ratings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    content_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    score = table.Column<int>(type: "integer", nullable: false),
                    note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    rated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_human_ratings", x => x.id);
                    table.ForeignKey(
                        name: "fk_human_ratings_content_items_content_item_id",
                        column: x => x.content_item_id,
                        principalTable: "content_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_campaign_packages_campaign_id",
                table: "campaign_packages",
                column: "campaign_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_human_ratings_content_item_id",
                table: "human_ratings",
                column: "content_item_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "campaign_packages");

            migrationBuilder.DropTable(
                name: "human_ratings");
        }
    }
}
