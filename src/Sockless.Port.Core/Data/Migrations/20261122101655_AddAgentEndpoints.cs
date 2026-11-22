using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sockless.Port.Data.Migrations;

/// <inheritdoc />
public partial class _20261122101655_AddAgentEndpoints : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "AgentCertificateNotAfter",
            table: "Endpoints",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "AgentCertificatePem",
            table: "Endpoints",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "AgentThumbprint",
            table: "Endpoints",
            type: "TEXT",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "EnrolledAt",
            table: "Endpoints",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "EnrolmentExpiresAt",
            table: "Endpoints",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "EnrolmentTokenId",
            table: "Endpoints",
            type: "TEXT",
            maxLength: 32,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "Kind",
            table: "Endpoints",
            type: "TEXT",
            maxLength: 16,
            nullable: false,
            defaultValue: "Local");

        // Before Phase 6 every endpoint was the local one.
        migrationBuilder.Sql("UPDATE \"Endpoints\" SET \"Kind\" = CASE WHEN \"IsLocal\" THEN 'Local' ELSE 'Agent' END;");

        // IsLocal is dropped in the next migration: on SQLite that rebuilds the table, which can't
        // run in this migration's transaction.

        migrationBuilder.AddColumn<string>(
            name: "ProtectedEnrolmentSecret",
            table: "Endpoints",
            type: "TEXT",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("UPDATE \"Endpoints\" SET \"IsLocal\" = \"Kind\" = 'Local';");

        migrationBuilder.DropColumn(
            name: "AgentCertificateNotAfter",
            table: "Endpoints");

        migrationBuilder.DropColumn(
            name: "AgentCertificatePem",
            table: "Endpoints");

        migrationBuilder.DropColumn(
            name: "AgentThumbprint",
            table: "Endpoints");

        migrationBuilder.DropColumn(
            name: "EnrolledAt",
            table: "Endpoints");

        migrationBuilder.DropColumn(
            name: "EnrolmentExpiresAt",
            table: "Endpoints");

        migrationBuilder.DropColumn(
            name: "EnrolmentTokenId",
            table: "Endpoints");

        migrationBuilder.DropColumn(
            name: "Kind",
            table: "Endpoints");

        migrationBuilder.DropColumn(
            name: "ProtectedEnrolmentSecret",
            table: "Endpoints");

    }
}
