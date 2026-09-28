using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Audit.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddChainHeadsAndTextPayload : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "payload",
                table: "events",
                type: "longtext",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "json")
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "engagement_chain_heads",
                columns: table => new
                {
                    engagement_id = table.Column<Guid>(type: "char(36)", maxLength: 36, nullable: false, collation: "ascii_general_ci"),
                    tenant_id = table.Column<Guid>(type: "char(36)", maxLength: 36, nullable: false, collation: "ascii_general_ci"),
                    last_event_id = table.Column<Guid>(type: "char(36)", maxLength: 36, nullable: true, collation: "ascii_general_ci"),
                    last_hash = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    updated_at = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_engagement_chain_heads", x => x.engagement_id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "engagement_chain_heads");

            migrationBuilder.AlterColumn<string>(
                name: "payload",
                table: "events",
                type: "json",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "longtext")
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");
        }
    }
}
