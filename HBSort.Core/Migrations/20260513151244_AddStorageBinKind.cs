using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HBSort.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddStorageBinKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Kind",
                table: "StorageBins",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            // v0.1.23: Index fuer schnelle Filter-Queries
            // (GetEligibleBinsAsync WHERE Kind IN ...).
            migrationBuilder.CreateIndex(
                name: "IX_StorageBins_Kind",
                table: "StorageBins",
                column: "Kind");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_StorageBins_Kind",
                table: "StorageBins");
            migrationBuilder.DropColumn(
                name: "Kind",
                table: "StorageBins");
        }
    }
}
