using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HBSort.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddBrickognizeCategoryToFloatingPart : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BrickognizeCategory",
                table: "FloatingParts",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BrickognizeCategory",
                table: "FloatingParts");
        }
    }
}
