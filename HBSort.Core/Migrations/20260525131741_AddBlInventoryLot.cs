using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HBSort.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddBlInventoryLot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BlInventoryLots",
                columns: table => new
                {
                    LotId = table.Column<int>(type: "INTEGER", nullable: false),
                    ItemType = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    ItemNo = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ColorId = table.Column<int>(type: "INTEGER", nullable: true),
                    ColorName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Quantity = table.Column<int>(type: "INTEGER", nullable: false),
                    UnitPrice = table.Column<decimal>(type: "TEXT", nullable: false),
                    Condition = table.Column<string>(type: "TEXT", maxLength: 2, nullable: false),
                    ReservedQuantity = table.Column<int>(type: "INTEGER", nullable: false),
                    LastSyncedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BlInventoryLots", x => x.LotId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BlInventoryLots_ItemType_ItemNo_ColorId",
                table: "BlInventoryLots",
                columns: new[] { "ItemType", "ItemNo", "ColorId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BlInventoryLots");
        }
    }
}
