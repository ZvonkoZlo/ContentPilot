using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContentPilot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BrandBrain : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audience_personas",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    brand_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    segment = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    detail = table.Column<string>(type: "jsonb", nullable: false),
                    is_primary = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audience_personas", x => x.id);
                    table.ForeignKey(
                        name: "fk_audience_personas_brands_brand_id",
                        column: x => x.brand_id,
                        principalTable: "brands",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "brand_assets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    brand_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<short>(type: "smallint", nullable: false),
                    file_name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    storage_key = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    media_type = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    bytes = table.Column<long>(type: "bigint", nullable: false),
                    width = table.Column<int>(type: "integer", nullable: false),
                    height = table.Column<int>(type: "integer", nullable: false),
                    origin = table.Column<short>(type: "smallint", nullable: false),
                    parent_asset_id = table.Column<Guid>(type: "uuid", nullable: true),
                    variant = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    perceptual_hash = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    is_archived = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    dominant_colors = table.Column<List<string>>(type: "text[]", nullable: false),
                    tags = table.Column<List<string>>(type: "text[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_brand_assets", x => x.id);
                    table.ForeignKey(
                        name: "fk_brand_assets_brand_assets_parent_asset_id",
                        column: x => x.parent_asset_id,
                        principalTable: "brand_assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_brand_assets_brands_brand_id",
                        column: x => x.brand_id,
                        principalTable: "brands",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "brand_profile_versions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    brand_id = table.Column<Guid>(type: "uuid", nullable: false),
                    snapshot_json = table.Column<string>(type: "jsonb", nullable: false),
                    content_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_brand_profile_versions", x => x.id);
                    table.ForeignKey(
                        name: "fk_brand_profile_versions_brands_brand_id",
                        column: x => x.brand_id,
                        principalTable: "brands",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "brand_profiles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    brand_id = table.Column<Guid>(type: "uuid", nullable: false),
                    visual = table.Column<string>(type: "jsonb", nullable: false),
                    voice = table.Column<string>(type: "jsonb", nullable: false),
                    messaging = table.Column<string>(type: "jsonb", nullable: false),
                    operator_notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_brand_profiles", x => x.id);
                    table.ForeignKey(
                        name: "fk_brand_profiles_brands_brand_id",
                        column: x => x.brand_id,
                        principalTable: "brands",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "content_preferences",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    brand_id = table.Column<Guid>(type: "uuid", nullable: false),
                    posts_per_week = table.Column<int>(type: "integer", nullable: false),
                    carousels_per_week = table.Column<int>(type: "integer", nullable: false),
                    reels_per_week = table.Column<int>(type: "integer", nullable: false),
                    generation_day = table.Column<short>(type: "smallint", nullable: false),
                    generation_time = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    scheduled_generation_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    excluded_topics = table.Column<List<string>>(type: "text[]", nullable: false),
                    preferred_topics = table.Column<List<string>>(type: "text[]", nullable: false),
                    publish_days = table.Column<int[]>(type: "integer[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_content_preferences", x => x.id);
                    table.ForeignKey(
                        name: "fk_content_preferences_brands_brand_id",
                        column: x => x.brand_id,
                        principalTable: "brands",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "industry_profiles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    detail = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_industry_profiles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "product_facts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    brand_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    statement = table.Column<string>(type: "character varying(600)", maxLength: 600, nullable: false),
                    category = table.Column<short>(type: "smallint", nullable: false),
                    evidence = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    is_public = table.Column<bool>(type: "boolean", nullable: false),
                    valid_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    valid_to = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_product_facts", x => x.id);
                    table.ForeignKey(
                        name: "fk_product_facts_brands_brand_id",
                        column: x => x.brand_id,
                        principalTable: "brands",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_audience_personas_brand_id",
                table: "audience_personas",
                column: "brand_id");

            migrationBuilder.CreateIndex(
                name: "ix_audience_personas_tenant_id_brand_id_is_primary",
                table: "audience_personas",
                columns: new[] { "tenant_id", "brand_id", "is_primary" });

            migrationBuilder.CreateIndex(
                name: "ix_brand_assets_content",
                table: "brand_assets",
                columns: new[] { "brand_id", "sha256", "variant" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_brand_assets_parent_asset_id",
                table: "brand_assets",
                column: "parent_asset_id");

            migrationBuilder.CreateIndex(
                name: "ix_brand_assets_tenant_id_brand_id_kind",
                table: "brand_assets",
                columns: new[] { "tenant_id", "brand_id", "kind" });

            migrationBuilder.CreateIndex(
                name: "ix_brand_profile_versions_brand_id_content_hash",
                table: "brand_profile_versions",
                columns: new[] { "brand_id", "content_hash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_brand_profile_versions_tenant_id_brand_id_created_at",
                table: "brand_profile_versions",
                columns: new[] { "tenant_id", "brand_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_brand_profiles_brand_id",
                table: "brand_profiles",
                column: "brand_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_content_preferences_brand_id",
                table: "content_preferences",
                column: "brand_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_content_preferences_scheduled",
                table: "content_preferences",
                column: "generation_day",
                filter: "scheduled_generation_enabled");

            migrationBuilder.CreateIndex(
                name: "ix_industry_profiles_key",
                table: "industry_profiles",
                column: "key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_product_facts_brand_id_key",
                table: "product_facts",
                columns: new[] { "brand_id", "key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audience_personas");

            migrationBuilder.DropTable(
                name: "brand_assets");

            migrationBuilder.DropTable(
                name: "brand_profile_versions");

            migrationBuilder.DropTable(
                name: "brand_profiles");

            migrationBuilder.DropTable(
                name: "content_preferences");

            migrationBuilder.DropTable(
                name: "industry_profiles");

            migrationBuilder.DropTable(
                name: "product_facts");
        }
    }
}
