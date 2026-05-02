using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LegoMinifigSorter.Core.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DailyStats",
                columns: table => new
                {
                    Date = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ScanCount = table.Column<int>(type: "INTEGER", nullable: false),
                    MinifigsCompletedCount = table.Column<int>(type: "INTEGER", nullable: false),
                    MinifigsDismantledCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DailyStats", x => x.Date);
                });

            migrationBuilder.CreateTable(
                name: "ScanEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    RecognizedId = table.Column<string>(type: "TEXT", nullable: true),
                    Confidence = table.Column<double>(type: "REAL", nullable: true),
                    ImagePath = table.Column<string>(type: "TEXT", nullable: true),
                    ResultDescription = table.Column<string>(type: "TEXT", nullable: false),
                    WasUndone = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScanEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StorageBins",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Label = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FreedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StorageBins", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FloatingParts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PartNumber = table.Column<string>(type: "TEXT", nullable: false),
                    ColorId = table.Column<int>(type: "INTEGER", nullable: false),
                    ColorName = table.Column<string>(type: "TEXT", nullable: false),
                    PartName = table.Column<string>(type: "TEXT", nullable: false),
                    Quantity = table.Column<int>(type: "INTEGER", nullable: false),
                    StorageBinId = table.Column<int>(type: "INTEGER", nullable: false),
                    AddedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FloatingParts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FloatingParts_StorageBins_StorageBinId",
                        column: x => x.StorageBinId,
                        principalTable: "StorageBins",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TrackedMinifigs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    FigNum = table.Column<string>(type: "TEXT", nullable: false),
                    BricklinkId = table.Column<string>(type: "TEXT", nullable: true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    ImageUrl = table.Column<string>(type: "TEXT", nullable: true),
                    LocalImagePath = table.Column<string>(type: "TEXT", nullable: true),
                    UserNotes = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    StorageBinId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrackedMinifigs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TrackedMinifigs_StorageBins_StorageBinId",
                        column: x => x.StorageBinId,
                        principalTable: "StorageBins",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "TrackedMinifigParts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TrackedMinifigId = table.Column<int>(type: "INTEGER", nullable: false),
                    PartNumber = table.Column<string>(type: "TEXT", nullable: false),
                    ColorId = table.Column<int>(type: "INTEGER", nullable: false),
                    ColorName = table.Column<string>(type: "TEXT", nullable: false),
                    PartName = table.Column<string>(type: "TEXT", nullable: false),
                    QuantityNeeded = table.Column<int>(type: "INTEGER", nullable: false),
                    QuantityCollected = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrackedMinifigParts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TrackedMinifigParts_TrackedMinifigs_TrackedMinifigId",
                        column: x => x.TrackedMinifigId,
                        principalTable: "TrackedMinifigs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FloatingParts_StorageBinId",
                table: "FloatingParts",
                column: "StorageBinId");

            migrationBuilder.CreateIndex(
                name: "IX_TrackedMinifigParts_TrackedMinifigId",
                table: "TrackedMinifigParts",
                column: "TrackedMinifigId");

            migrationBuilder.CreateIndex(
                name: "IX_TrackedMinifigs_StorageBinId",
                table: "TrackedMinifigs",
                column: "StorageBinId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DailyStats");

            migrationBuilder.DropTable(
                name: "FloatingParts");

            migrationBuilder.DropTable(
                name: "ScanEvents");

            migrationBuilder.DropTable(
                name: "TrackedMinifigParts");

            migrationBuilder.DropTable(
                name: "TrackedMinifigs");

            migrationBuilder.DropTable(
                name: "StorageBins");
        }
    }
}
