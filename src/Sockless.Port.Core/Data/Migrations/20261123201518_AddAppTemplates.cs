using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sockless.Port.Data.Migrations;

/// <inheritdoc />
public partial class _20261123201518_AddAppTemplates : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "Template",
            table: "Stacks",
            type: "TEXT",
            maxLength: 200,
            nullable: true);

        migrationBuilder.CreateTable(
            name: "TemplateSources",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                Url = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                FetchedAt = table.Column<string>(type: "TEXT", nullable: true),
                AttemptedAt = table.Column<string>(type: "TEXT", nullable: true),
                Error = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                TemplateCount = table.Column<int>(type: "INTEGER", nullable: false),
                Format = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_TemplateSources", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_TemplateSources_Name",
            table: "TemplateSources",
            column: "Name",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "TemplateSources");

        migrationBuilder.DropColumn(
            name: "Template",
            table: "Stacks");
    }
}
