using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HBSort.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddBricklinkColorId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "BricklinkColorId",
                table: "TrackedMinifigParts",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BricklinkColorId",
                table: "FloatingParts",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BricklinkColorId",
                table: "TrackedMinifigParts");

            migrationBuilder.DropColumn(
                name: "BricklinkColorId",
                table: "FloatingParts");
        }
    }
}
