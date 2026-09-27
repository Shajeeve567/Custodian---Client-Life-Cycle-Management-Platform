using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workflow.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddStallRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "stall_records",
                columns: table => new
                {
                    stall_id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    tenant_id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    engagement_id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    action_id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    open_action_id = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    due_at_utc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    detected_at_utc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    overdue_event_published_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    resolved_at_utc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    resolution = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stall_records", x => x.stall_id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "idx_stall_action_resolved",
                table: "stall_records",
                columns: new[] { "action_id", "resolved_at_utc" });

            migrationBuilder.CreateIndex(
                name: "idx_stall_engagement",
                table: "stall_records",
                column: "engagement_id");

            migrationBuilder.CreateIndex(
                name: "idx_stall_tenant_resolved",
                table: "stall_records",
                columns: new[] { "tenant_id", "resolved_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ux_stall_open_action",
                table: "stall_records",
                column: "open_action_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "stall_records");
        }
    }
}
