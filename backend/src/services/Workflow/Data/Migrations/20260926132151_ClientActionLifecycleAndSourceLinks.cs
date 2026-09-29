using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workflow.Data.Migrations
{
    /// <inheritdoc />
    public partial class ClientActionLifecycleAndSourceLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "activated_at",
                table: "client_actions",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "linked_condition_id",
                table: "client_actions",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci");

            migrationBuilder.AddColumn<Guid>(
                name: "linked_document_id",
                table: "client_actions",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci");

            migrationBuilder.AddColumn<Guid>(
                name: "linked_meeting_id",
                table: "client_actions",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci");

            migrationBuilder.AddColumn<string>(
                name: "source_type",
                table: "client_actions",
                type: "varchar(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<DateTime>(
                name: "updated_at",
                table: "client_actions",
                type: "datetime(6)",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            // CSTD-21 (21-N1) backfill for rows created before this migration.
            migrationBuilder.Sql("UPDATE client_actions SET updated_at = created_at;");

            migrationBuilder.Sql(
                "UPDATE client_actions SET source_type = 'Requirement' " +
                "WHERE source_type = '' AND linked_requirement_id IS NOT NULL;");
            migrationBuilder.Sql(
                "UPDATE client_actions SET source_type = 'Lifecycle' " +
                "WHERE source_type = '' AND source = 'LifecycleDefault';");
            migrationBuilder.Sql("UPDATE client_actions SET source_type = 'Manual' WHERE source_type = '';");

            // Document link previously lived only in the source_metadata JSON (documentId).
            migrationBuilder.Sql(
                "UPDATE client_actions " +
                "SET linked_document_id = JSON_UNQUOTE(JSON_EXTRACT(source_metadata, '$.documentId')) " +
                "WHERE linked_document_id IS NULL " +
                "AND source_metadata IS NOT NULL AND JSON_VALID(source_metadata) " +
                "AND JSON_UNQUOTE(JSON_EXTRACT(source_metadata, '$.documentId')) " +
                "REGEXP '^[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}$';");

            // Actions at or below the engagement's current stage were already actionable (stage enum + 1).
            migrationBuilder.Sql(
                "UPDATE client_actions a " +
                "JOIN engagements e ON e.engagement_id = a.engagement_id AND e.tenant_id = a.tenant_id " +
                "SET a.activated_at = a.created_at " +
                "WHERE a.activated_at IS NULL AND a.stage_number <= (CASE e.stage " +
                "WHEN 'Onboarding' THEN 1 WHEN 'DocumentCollection' THEN 2 WHEN 'Verification' THEN 3 " +
                "WHEN 'Execution' THEN 4 WHEN 'Closure' THEN 5 ELSE 1 END);");

            migrationBuilder.CreateIndex(
                name: "idx_action_linked_document",
                table: "client_actions",
                column: "linked_document_id");

            migrationBuilder.CreateIndex(
                name: "idx_action_tenant_engagement_status",
                table: "client_actions",
                columns: new[] { "tenant_id", "engagement_id", "status" });

            migrationBuilder.CreateIndex(
                name: "idx_action_tenant_status_deadline",
                table: "client_actions",
                columns: new[] { "tenant_id", "status", "deadline_utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_action_linked_document",
                table: "client_actions");

            migrationBuilder.DropIndex(
                name: "idx_action_tenant_engagement_status",
                table: "client_actions");

            migrationBuilder.DropIndex(
                name: "idx_action_tenant_status_deadline",
                table: "client_actions");

            migrationBuilder.DropColumn(
                name: "activated_at",
                table: "client_actions");

            migrationBuilder.DropColumn(
                name: "linked_condition_id",
                table: "client_actions");

            migrationBuilder.DropColumn(
                name: "linked_document_id",
                table: "client_actions");

            migrationBuilder.DropColumn(
                name: "linked_meeting_id",
                table: "client_actions");

            migrationBuilder.DropColumn(
                name: "source_type",
                table: "client_actions");

            migrationBuilder.DropColumn(
                name: "updated_at",
                table: "client_actions");
        }
    }
}
