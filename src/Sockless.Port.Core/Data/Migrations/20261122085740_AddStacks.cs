using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sockless.Port.Data.Migrations;

/// <inheritdoc />
public partial class _20261122085740_AddStacks : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Stacks",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                EndpointId = table.Column<int>(type: "INTEGER", nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 63, nullable: false),
                Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                Adopted = table.Column<bool>(type: "INTEGER", nullable: false),
                CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<string>(type: "TEXT", nullable: false),
                DeployedAt = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Stacks", x => x.Id);
                table.ForeignKey(
                    name: "FK_Stacks_Endpoints_EndpointId",
                    column: x => x.EndpointId,
                    principalTable: "Endpoints",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_Stacks_EndpointId_Name",
            table: "Stacks",
            columns: new[] { "EndpointId", "Name" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "Stacks");
    }
}
