using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Audit.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPreviousHash : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "previous_hash",
                table: "events",
                type: "varchar(64)",
                maxLength: 64,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "idx_tenant_sequence",
                table: "events",
                columns: new[] { "tenant_id", "sequence_number" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_tenant_sequence",
                table: "events");

            migrationBuilder.DropColumn(
                name: "previous_hash",
                table: "events");
        }
    }
}