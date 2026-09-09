using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workflow.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDeadlineAndStageToClientActions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "deadline_utc",
                table: "client_actions",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "stage_number",
                table: "client_actions",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateIndex(
                name: "idx_action_engagement_stage",
                table: "client_actions",
                columns: new[] { "engagement_id", "stage_number" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_action_engagement_stage",
                table: "client_actions");

            migrationBuilder.DropColumn(
                name: "deadline_utc",
                table: "client_actions");

            migrationBuilder.DropColumn(
                name: "stage_number",
                table: "client_actions");
        }
    }
}
