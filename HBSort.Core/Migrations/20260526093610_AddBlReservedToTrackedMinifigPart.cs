using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HBSort.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddBlReservedToTrackedMinifigPart : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "QuantityReservedFromBl",
                table: "TrackedMinifigParts",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "QuantityReservedFromBl",
                table: "TrackedMinifigParts");
        }
    }
}
