using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HBSort.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddIgnoredBuildSuggestion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "IgnoredBuildSuggestions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BricklinkId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    IgnoredAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IgnoredBuildSuggestions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IgnoredBuildSuggestions_BricklinkId",
                table: "IgnoredBuildSuggestions",
                column: "BricklinkId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IgnoredBuildSuggestions");
        }
    }
}
