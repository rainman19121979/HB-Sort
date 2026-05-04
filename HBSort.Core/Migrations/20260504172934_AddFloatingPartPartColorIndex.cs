using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HBSort.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddFloatingPartPartColorIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_FloatingParts_PartNumber_ColorId",
                table: "FloatingParts",
                columns: new[] { "PartNumber", "ColorId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_FloatingParts_PartNumber_ColorId",
                table: "FloatingParts");
        }
    }
}
