using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Documents.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentComplianceFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "compliance_status",
                table: "documents",
                type: "varchar(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "Pending")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "rejection_reason",
                table: "documents",
                type: "varchar(1000)",
                maxLength: 1000,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<DateTime>(
                name: "validated_at",
                table: "documents",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_documents_compliance_status",
                table: "documents",
                column: "compliance_status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_documents_compliance_status",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "compliance_status",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "rejection_reason",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "validated_at",
                table: "documents");
        }
    }
}
