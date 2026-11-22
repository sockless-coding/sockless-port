using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sockless.Port.Data.Migrations;

/// <inheritdoc />
public partial class _20261121213914_AddRegistries : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Registries",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                Registry = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                Username = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                ProtectedPassword = table.Column<string>(type: "TEXT", nullable: false),
                CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Registries", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_Registries_Registry",
            table: "Registries",
            column: "Registry",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "Registries");
    }
}
