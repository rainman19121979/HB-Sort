using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LegoMinifigSorter.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddOriginMinifigIdToFloatingPart : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "OriginMinifigId",
                table: "FloatingParts",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_FloatingParts_OriginMinifigId",
                table: "FloatingParts",
                column: "OriginMinifigId");

            migrationBuilder.AddForeignKey(
                name: "FK_FloatingParts_TrackedMinifigs_OriginMinifigId",
                table: "FloatingParts",
                column: "OriginMinifigId",
                principalTable: "TrackedMinifigs",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_FloatingParts_TrackedMinifigs_OriginMinifigId",
                table: "FloatingParts");

            migrationBuilder.DropIndex(
                name: "IX_FloatingParts_OriginMinifigId",
                table: "FloatingParts");

            migrationBuilder.DropColumn(
                name: "OriginMinifigId",
                table: "FloatingParts");
        }
    }
}
