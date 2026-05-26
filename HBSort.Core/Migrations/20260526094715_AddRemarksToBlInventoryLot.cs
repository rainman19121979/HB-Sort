using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HBSort.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddRemarksToBlInventoryLot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Remarks",
                table: "BlInventoryLots",
                type: "TEXT",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Remarks",
                table: "BlInventoryLots");
        }
    }
}
