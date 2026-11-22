using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sockless.Port.Data.Migrations;

/// <inheritdoc />
public partial class _20261122185418_AddImageScans : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ImageScans",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                ImageId = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                ImageName = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                ScannedAt = table.Column<string>(type: "TEXT", nullable: false),
                ScannerVersion = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                DatabaseUpdatedAt = table.Column<string>(type: "TEXT", nullable: true),
                Os = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                OsEndOfLife = table.Column<bool>(type: "INTEGER", nullable: false),
                Critical = table.Column<int>(type: "INTEGER", nullable: false),
                High = table.Column<int>(type: "INTEGER", nullable: false),
                Medium = table.Column<int>(type: "INTEGER", nullable: false),
                Low = table.Column<int>(type: "INTEGER", nullable: false),
                Unknown = table.Column<int>(type: "INTEGER", nullable: false),
                Fixable = table.Column<int>(type: "INTEGER", nullable: false),
                Findings = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ImageScans", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_ImageScans_ImageId",
            table: "ImageScans",
            column: "ImageId",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "ImageScans");
    }
}
