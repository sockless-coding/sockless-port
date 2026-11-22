using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sockless.Port.Data.Migrations;

/// <summary>Replaced by Kind in the previous migration. Alone here because SQLite rebuilds the table to drop a column.</summary>
public partial class _20261122101656_DropEndpointIsLocal : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "IsLocal",
            table: "Endpoints");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "IsLocal",
            table: "Endpoints",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);
    }
}
