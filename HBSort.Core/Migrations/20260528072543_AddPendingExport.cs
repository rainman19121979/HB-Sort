using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HBSort.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddPendingExport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PendingExports",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    LotId = table.Column<int>(type: "INTEGER", nullable: false),
                    ExpectedQuantity = table.Column<int>(type: "INTEGER", nullable: false),
                    ShouldDelete = table.Column<bool>(type: "INTEGER", nullable: false),
                    ExportedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingExports", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PendingExports_LotId",
                table: "PendingExports",
                column: "LotId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PendingExports");
        }
    }
}
