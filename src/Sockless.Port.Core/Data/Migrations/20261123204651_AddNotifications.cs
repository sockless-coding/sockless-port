using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sockless.Port.Data.Migrations;

/// <inheritdoc />
public partial class _20261123204651_AddNotifications : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "NotificationChannels",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                Target = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                ProtectedSecret = table.Column<string>(type: "TEXT", nullable: true),
                CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<string>(type: "TEXT", nullable: false),
                LastSentAt = table.Column<string>(type: "TEXT", nullable: true),
                LastFailedAt = table.Column<string>(type: "TEXT", nullable: true),
                LastError = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_NotificationChannels", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "NotificationHistory",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                At = table.Column<string>(type: "TEXT", nullable: false),
                Kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                Severity = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                Resolved = table.Column<bool>(type: "INTEGER", nullable: false),
                Test = table.Column<bool>(type: "INTEGER", nullable: false),
                EndpointName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                Title = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                Message = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                ChannelName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                RuleName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                Outcome = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                Error = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_NotificationHistory", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "NotificationRules",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                ChannelId = table.Column<int>(type: "INTEGER", nullable: false),
                EndpointId = table.Column<int>(type: "INTEGER", nullable: true),
                Kinds = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                QuietMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                SendResolved = table.Column<bool>(type: "INTEGER", nullable: false),
                CpuPercent = table.Column<int>(type: "INTEGER", nullable: true),
                MemoryPercent = table.Column<int>(type: "INTEGER", nullable: true),
                ForMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                CreatedAt = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_NotificationRules", x => x.Id);
                table.ForeignKey(
                    name: "FK_NotificationRules_Endpoints_EndpointId",
                    column: x => x.EndpointId,
                    principalTable: "Endpoints",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_NotificationRules_NotificationChannels_ChannelId",
                    column: x => x.ChannelId,
                    principalTable: "NotificationChannels",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_NotificationChannels_Name",
            table: "NotificationChannels",
            column: "Name",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_NotificationHistory_At",
            table: "NotificationHistory",
            column: "At");

        migrationBuilder.CreateIndex(
            name: "IX_NotificationRules_ChannelId",
            table: "NotificationRules",
            column: "ChannelId");

        migrationBuilder.CreateIndex(
            name: "IX_NotificationRules_EndpointId",
            table: "NotificationRules",
            column: "EndpointId");

        migrationBuilder.CreateIndex(
            name: "IX_NotificationRules_Name",
            table: "NotificationRules",
            column: "Name",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "NotificationHistory");

        migrationBuilder.DropTable(
            name: "NotificationRules");

        migrationBuilder.DropTable(
            name: "NotificationChannels");
    }
}
