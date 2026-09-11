using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pointframe.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCaptureCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "capture_artifacts",
                columns: table => new
                {
                    artifact_id = table.Column<string>(type: "TEXT", nullable: false),
                    kind = table.Column<string>(type: "TEXT", nullable: false),
                    mime_type = table.Column<string>(type: "TEXT", nullable: false),
                    file_name = table.Column<string>(type: "TEXT", nullable: false),
                    sha256 = table.Column<string>(type: "TEXT", nullable: false),
                    byte_length = table.Column<long>(type: "INTEGER", nullable: false),
                    pixel_width = table.Column<int>(type: "INTEGER", nullable: false),
                    pixel_height = table.Column<int>(type: "INTEGER", nullable: false),
                    captured_at_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    timestamp_source = table.Column<string>(type: "TEXT", nullable: false),
                    source = table.Column<string>(type: "TEXT", nullable: false),
                    provenance_json = table.Column<string>(type: "TEXT", nullable: true),
                    availability = table.Column<string>(type: "TEXT", nullable: false),
                    ocr_status = table.Column<string>(type: "TEXT", nullable: false),
                    ocr_text = table.Column<string>(type: "TEXT", nullable: true),
                    search_text_normalized = table.Column<string>(type: "TEXT", nullable: true),
                    indexed_sha256 = table.Column<string>(type: "TEXT", nullable: true),
                    ocr_engine_version = table.Column<string>(type: "TEXT", nullable: true),
                    indexed_at_utc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    attempt_count = table.Column<int>(type: "INTEGER", nullable: false),
                    next_attempt_utc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    lease_owner = table.Column<string>(type: "TEXT", nullable: true),
                    lease_expires_utc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_capture_artifacts", x => x.artifact_id);
                });

            migrationBuilder.CreateTable(
                name: "capture_locations",
                columns: table => new
                {
                    normalized_path = table.Column<string>(type: "TEXT", nullable: false),
                    original_path = table.Column<string>(type: "TEXT", nullable: false),
                    current_artifact_id = table.Column<string>(type: "TEXT", nullable: true),
                    last_write_at_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    byte_length = table.Column<long>(type: "INTEGER", nullable: false),
                    last_observed_at_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_capture_locations", x => x.normalized_path);
                    table.ForeignKey(
                        name: "FK_capture_locations_capture_artifacts_current_artifact_id",
                        column: x => x.current_artifact_id,
                        principalTable: "capture_artifacts",
                        principalColumn: "artifact_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_capture_artifacts_availability",
                table: "capture_artifacts",
                column: "availability");

            migrationBuilder.CreateIndex(
                name: "ix_capture_artifacts_captured_at_utc_artifact_id",
                table: "capture_artifacts",
                columns: new[] { "captured_at_utc", "artifact_id" });

            migrationBuilder.CreateIndex(
                name: "ix_capture_artifacts_ocr_work",
                table: "capture_artifacts",
                columns: new[] { "ocr_status", "next_attempt_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_capture_locations_current_artifact_id",
                table: "capture_locations",
                column: "current_artifact_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "capture_locations");

            migrationBuilder.DropTable(
                name: "capture_artifacts");
        }
    }
}
