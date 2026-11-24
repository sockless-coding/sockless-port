using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sockless.Port.Data.Migrations;

/// <inheritdoc />
public partial class _20261124174247_AddGitStacks : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "StackRepositories",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                StackId = table.Column<int>(type: "INTEGER", nullable: false),
                Url = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                Branch = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                ComposePath = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                EnvPath = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                Auth = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                Username = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                ProtectedSecret = table.Column<string>(type: "TEXT", nullable: true),
                PublicKey = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                KnownHosts = table.Column<string>(type: "TEXT", nullable: true),
                PollMinutes = table.Column<int>(type: "INTEGER", nullable: true),
                WebhookId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                ProtectedWebhookSecret = table.Column<string>(type: "TEXT", nullable: true),
                FetchedCommit = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                FetchedSubject = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                FetchedCommitAt = table.Column<string>(type: "TEXT", nullable: true),
                FetchedAt = table.Column<string>(type: "TEXT", nullable: true),
                DeployedCommit = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                DeployedSubject = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                CheckedAt = table.Column<string>(type: "TEXT", nullable: true),
                LastError = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_StackRepositories", x => x.Id);
                table.ForeignKey(
                    name: "FK_StackRepositories_Stacks_StackId",
                    column: x => x.StackId,
                    principalTable: "Stacks",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_StackRepositories_StackId",
            table: "StackRepositories",
            column: "StackId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_StackRepositories_WebhookId",
            table: "StackRepositories",
            column: "WebhookId",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "StackRepositories");
    }
}
