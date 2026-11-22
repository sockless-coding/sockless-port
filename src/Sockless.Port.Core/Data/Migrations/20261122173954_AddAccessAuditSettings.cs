using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sockless.Port.Data.Migrations;

/// <inheritdoc />
public partial class _20261122173954_AddAccessAuditSettings : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "DefaultRole",
            table: "AspNetUsers",
            type: "TEXT",
            maxLength: 16,
            nullable: false,
            defaultValue: "None");

        migrationBuilder.AddColumn<bool>(
            name: "IsAdmin",
            table: "AspNetUsers",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<bool>(
            name: "IsDisabled",
            table: "AspNetUsers",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<string>(
            name: "LastSignInAt",
            table: "AspNetUsers",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "TimeDisplay",
            table: "AspNetUsers",
            type: "TEXT",
            maxLength: 8,
            nullable: true);

        // Before roles, every user was an administrator (the one created at setup).
        migrationBuilder.Sql("UPDATE AspNetUsers SET IsAdmin = 1");

        migrationBuilder.CreateTable(
            name: "AuditLog",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                At = table.Column<string>(type: "TEXT", nullable: false),
                UserId = table.Column<string>(type: "TEXT", nullable: true),
                UserName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                EndpointId = table.Column<int>(type: "INTEGER", nullable: true),
                EndpointName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                Action = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                Target = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                Outcome = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                Detail = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                RemoteAddress = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AuditLog", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "EndpointGrants",
            columns: table => new
            {
                UserId = table.Column<string>(type: "TEXT", nullable: false),
                EndpointId = table.Column<int>(type: "INTEGER", nullable: false),
                Role = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_EndpointGrants", x => new { x.UserId, x.EndpointId });
                table.ForeignKey(
                    name: "FK_EndpointGrants_AspNetUsers_UserId",
                    column: x => x.UserId,
                    principalTable: "AspNetUsers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_EndpointGrants_Endpoints_EndpointId",
                    column: x => x.EndpointId,
                    principalTable: "Endpoints",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "Settings",
            columns: table => new
            {
                Key = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                Value = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Settings", x => x.Key);
            });

        migrationBuilder.CreateIndex(
            name: "IX_AuditLog_At",
            table: "AuditLog",
            column: "At");

        migrationBuilder.CreateIndex(
            name: "IX_AuditLog_UserName",
            table: "AuditLog",
            column: "UserName");

        migrationBuilder.CreateIndex(
            name: "IX_EndpointGrants_EndpointId",
            table: "EndpointGrants",
            column: "EndpointId");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "AuditLog");

        migrationBuilder.DropTable(
            name: "EndpointGrants");

        migrationBuilder.DropTable(
            name: "Settings");

        migrationBuilder.DropColumn(
            name: "DefaultRole",
            table: "AspNetUsers");

        migrationBuilder.DropColumn(
            name: "IsAdmin",
            table: "AspNetUsers");

        migrationBuilder.DropColumn(
            name: "IsDisabled",
            table: "AspNetUsers");

        migrationBuilder.DropColumn(
            name: "LastSignInAt",
            table: "AspNetUsers");

        migrationBuilder.DropColumn(
            name: "TimeDisplay",
            table: "AspNetUsers");
    }
}
