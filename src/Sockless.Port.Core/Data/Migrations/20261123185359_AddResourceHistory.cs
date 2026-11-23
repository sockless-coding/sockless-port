using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sockless.Port.Data.Migrations;

/// <inheritdoc />
public partial class _20261123185359_AddResourceHistory : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ResourceTotals",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                EndpointId = table.Column<int>(type: "INTEGER", nullable: false),
                Time = table.Column<long>(type: "INTEGER", nullable: false),
                Seconds = table.Column<int>(type: "INTEGER", nullable: false),
                Samples = table.Column<int>(type: "INTEGER", nullable: false),
                Cpu = table.Column<double>(type: "REAL", nullable: false),
                CpuPeak = table.Column<double>(type: "REAL", nullable: false),
                Memory = table.Column<long>(type: "INTEGER", nullable: false),
                MemoryPeak = table.Column<long>(type: "INTEGER", nullable: false),
                NetworkRx = table.Column<double>(type: "REAL", nullable: false),
                NetworkTx = table.Column<double>(type: "REAL", nullable: false),
                BlockRead = table.Column<double>(type: "REAL", nullable: false),
                BlockWrite = table.Column<double>(type: "REAL", nullable: false),
                Cpus = table.Column<int>(type: "INTEGER", nullable: false),
                HostMemory = table.Column<long>(type: "INTEGER", nullable: false),
                Containers = table.Column<int>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ResourceTotals", x => x.Id);
                table.ForeignKey(
                    name: "FK_ResourceTotals_Endpoints_EndpointId",
                    column: x => x.EndpointId,
                    principalTable: "Endpoints",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "ResourceUsage",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                EndpointId = table.Column<int>(type: "INTEGER", nullable: false),
                Time = table.Column<long>(type: "INTEGER", nullable: false),
                Seconds = table.Column<int>(type: "INTEGER", nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                Stack = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                Samples = table.Column<int>(type: "INTEGER", nullable: false),
                Cpu = table.Column<double>(type: "REAL", nullable: false),
                CpuPeak = table.Column<double>(type: "REAL", nullable: false),
                Memory = table.Column<long>(type: "INTEGER", nullable: false),
                MemoryPeak = table.Column<long>(type: "INTEGER", nullable: false),
                NetworkRx = table.Column<double>(type: "REAL", nullable: false),
                NetworkTx = table.Column<double>(type: "REAL", nullable: false),
                BlockRead = table.Column<double>(type: "REAL", nullable: false),
                BlockWrite = table.Column<double>(type: "REAL", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ResourceUsage", x => x.Id);
                table.ForeignKey(
                    name: "FK_ResourceUsage_Endpoints_EndpointId",
                    column: x => x.EndpointId,
                    principalTable: "Endpoints",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_ResourceTotals_EndpointId_Seconds_Time",
            table: "ResourceTotals",
            columns: new[] { "EndpointId", "Seconds", "Time" });

        migrationBuilder.CreateIndex(
            name: "IX_ResourceUsage_EndpointId_Seconds_Name_Time",
            table: "ResourceUsage",
            columns: new[] { "EndpointId", "Seconds", "Name", "Time" });

        migrationBuilder.CreateIndex(
            name: "IX_ResourceUsage_EndpointId_Seconds_Time",
            table: "ResourceUsage",
            columns: new[] { "EndpointId", "Seconds", "Time" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "ResourceTotals");

        migrationBuilder.DropTable(
            name: "ResourceUsage");
    }
}
