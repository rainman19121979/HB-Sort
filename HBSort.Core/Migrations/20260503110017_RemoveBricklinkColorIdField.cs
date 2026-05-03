using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HBSort.Core.Migrations
{
    /// <inheritdoc />
    public partial class RemoveBricklinkColorIdField : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Schritt 1: Daten retten - vor dem DropColumn alle Eintraege bei denen
            // ColorId=0 ist und BricklinkColorId einen "echten" Wert hat den Wert
            // in ColorId uebernehmen. Tritt nur bei sehr alten Datenbestaenden auf
            // (vor Brickognize/PROMPT 6, als ColorId noch eine Rebrickable-ID war).
            migrationBuilder.Sql(@"
                UPDATE TrackedMinifigParts
                SET ColorId = BricklinkColorId
                WHERE ColorId = 0
                  AND BricklinkColorId IS NOT NULL
                  AND BricklinkColorId != 0;
            ");
            migrationBuilder.Sql(@"
                UPDATE FloatingParts
                SET ColorId = BricklinkColorId
                WHERE ColorId = 0
                  AND BricklinkColorId IS NOT NULL
                  AND BricklinkColorId != 0;
            ");

            // Schritt 2: Spalte droppen.
            migrationBuilder.DropColumn(
                name: "BricklinkColorId",
                table: "TrackedMinifigParts");

            migrationBuilder.DropColumn(
                name: "BricklinkColorId",
                table: "FloatingParts");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Down: Spalte wieder anlegen, aber leer (NULL). Die zuvor uebernommenen
            // Werte stehen jetzt in ColorId und werden beim Down nicht zurueckkopiert -
            // das ist absichtlich, weil wir die Doppelt-Speicherung nicht wieder einfuehren.
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
    }
}
