using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Documents.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentVerificationFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "verification_status",
                table: "documents",
                type: "varchar(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "Unverified")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "verified_by",
                table: "documents",
                type: "varchar(100)",
                maxLength: 100,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<DateTime>(
                name: "verified_at",
                table: "documents",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "verification_reason",
                table: "documents",
                type: "varchar(1000)",
                maxLength: 1000,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_documents_verification_status",
                table: "documents",
                column: "verification_status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_documents_verification_status",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "verification_status",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "verified_by",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "verified_at",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "verification_reason",
                table: "documents");
        }
    }
}
