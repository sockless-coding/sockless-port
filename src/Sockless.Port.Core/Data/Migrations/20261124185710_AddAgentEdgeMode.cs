using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sockless.Port.Data.Migrations;

/// <inheritdoc />
public partial class _20261124185710_AddAgentEdgeMode : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "AgentPreviousThumbprint",
            table: "Endpoints",
            type: "TEXT",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "Edge",
            table: "Endpoints",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<string>(
            name: "ServerUrl",
            table: "Endpoints",
            type: "TEXT",
            maxLength: 512,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "TunnelSeenAt",
            table: "Endpoints",
            type: "TEXT",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "AgentPreviousThumbprint",
            table: "Endpoints");

        migrationBuilder.DropColumn(
            name: "Edge",
            table: "Endpoints");

        migrationBuilder.DropColumn(
            name: "ServerUrl",
            table: "Endpoints");

        migrationBuilder.DropColumn(
            name: "TunnelSeenAt",
            table: "Endpoints");
    }
}
