using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Documents.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentSoftDeleteFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_deleted",
                table: "documents",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "deleted_at",
                table: "documents",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "deleted_by",
                table: "documents",
                type: "varchar(100)",
                maxLength: 100,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_documents_is_deleted",
                table: "documents",
                column: "is_deleted");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_documents_is_deleted",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "is_deleted",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "deleted_at",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "deleted_by",
                table: "documents");
        }
    }
}
