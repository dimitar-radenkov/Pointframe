using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pointframe.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initialize : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "capture_text_cache",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    file_path = table.Column<string>(type: "TEXT", nullable: false),
                    captured_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    text = table.Column<string>(type: "TEXT", nullable: true),
                    last_accessed_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_capture_text_cache", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_capture_text_cache_file_path",
                table: "capture_text_cache",
                column: "file_path",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_capture_text_cache_last_accessed_at",
                table: "capture_text_cache",
                column: "last_accessed_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "capture_text_cache");
        }
    }
}
