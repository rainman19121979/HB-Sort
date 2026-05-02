using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HBSort.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddStorageBin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Notes",
                table: "StorageBins",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_StorageBins_Label",
                table: "StorageBins",
                column: "Label",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_StorageBins_Label",
                table: "StorageBins");

            migrationBuilder.DropColumn(
                name: "Notes",
                table: "StorageBins");
        }
    }
}
