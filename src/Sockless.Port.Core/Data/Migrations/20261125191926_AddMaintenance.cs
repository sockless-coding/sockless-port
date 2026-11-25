using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sockless.Port.Data.Migrations;

/// <inheritdoc />
public partial class _20261125191926_AddMaintenance : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "MaintenanceRuns",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                Task = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                EndpointId = table.Column<int>(type: "INTEGER", nullable: true),
                EndpointName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                StartedAt = table.Column<string>(type: "TEXT", nullable: false),
                FinishedAt = table.Column<string>(type: "TEXT", nullable: false),
                Outcome = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                By = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                Summary = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_MaintenanceRuns", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "PruneSchedules",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                EndpointId = table.Column<int>(type: "INTEGER", nullable: false),
                IntervalHours = table.Column<int>(type: "INTEGER", nullable: false),
                Images = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                OlderThanDays = table.Column<int>(type: "INTEGER", nullable: false),
                Volumes = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                Networks = table.Column<bool>(type: "INTEGER", nullable: false),
                BuildCache = table.Column<bool>(type: "INTEGER", nullable: false),
                UpdatedAt = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PruneSchedules", x => x.Id);
                table.ForeignKey(
                    name: "FK_PruneSchedules_Endpoints_EndpointId",
                    column: x => x.EndpointId,
                    principalTable: "Endpoints",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_MaintenanceRuns_StartedAt",
            table: "MaintenanceRuns",
            column: "StartedAt");

        migrationBuilder.CreateIndex(
            name: "IX_MaintenanceRuns_Task_EndpointId",
            table: "MaintenanceRuns",
            columns: new[] { "Task", "EndpointId" });

        migrationBuilder.CreateIndex(
            name: "IX_PruneSchedules_EndpointId",
            table: "PruneSchedules",
            column: "EndpointId",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "MaintenanceRuns");

        migrationBuilder.DropTable(
            name: "PruneSchedules");
    }
}
