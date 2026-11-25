using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TriageDesk.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReconcileGitHubSources : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "RequiresFullScan",
                table: "Repositories",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "NeedsRecheck",
                table: "IssueSnapshots",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "PageSize",
                table: "ImportRuns",
                type: "integer",
                nullable: false,
                defaultValue: 30);

            migrationBuilder.AddColumn<string>(
                name: "Phase",
                table: "ImportRuns",
                type: "character varying(12)",
                maxLength: 12,
                nullable: false,
                defaultValue: "list");

            migrationBuilder.AddColumn<Guid>(
                name: "RecheckAfter",
                table: "ImportRuns",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "Since",
                table: "ImportRuns",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TransientFailures",
                table: "ImportRuns",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "IssueExternalKeys",
                columns: table => new
                {
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    GitHubId = table.Column<long>(type: "bigint", nullable: false),
                    IssueId = table.Column<Guid>(type: "uuid", nullable: false),
                    Current = table.Column<bool>(type: "boolean", nullable: false),
                    FirstSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IssueExternalKeys", x => new { x.WorkspaceId, x.GitHubId });
                    table.ForeignKey(
                        name: "FK_IssueExternalKeys_IssueSnapshots_WorkspaceId_IssueId",
                        columns: x => new { x.WorkspaceId, x.IssueId },
                        principalTable: "IssueSnapshots",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IssueExternalKeys_WorkspaceId_IssueId",
                table: "IssueExternalKeys",
                columns: new[] { "WorkspaceId", "IssueId" },
                unique: true,
                filter: " \"Current\" ");

            migrationBuilder.Sql("""
                INSERT INTO "IssueExternalKeys" ("WorkspaceId", "GitHubId", "IssueId", "Current", "FirstSeenAt")
                SELECT "WorkspaceId", "GitHubId", "Id", true, "ObservedAt" FROM "IssueSnapshots";

                UPDATE "Repositories" r SET "RequiresFullScan" = true
                WHERE r."DisconnectedAt" IS NOT NULL OR NOT r."Public" OR EXISTS (
                    SELECT 1 FROM "IssueSnapshots" i
                    WHERE (i."WorkspaceId", i."RepositoryId") = (r."WorkspaceId", r."Id")
                        AND NOT i."SourceAvailable");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IssueExternalKeys");

            migrationBuilder.DropColumn(
                name: "RequiresFullScan",
                table: "Repositories");

            migrationBuilder.DropColumn(
                name: "NeedsRecheck",
                table: "IssueSnapshots");

            migrationBuilder.DropColumn(
                name: "PageSize",
                table: "ImportRuns");

            migrationBuilder.DropColumn(
                name: "Phase",
                table: "ImportRuns");

            migrationBuilder.DropColumn(
                name: "RecheckAfter",
                table: "ImportRuns");

            migrationBuilder.DropColumn(
                name: "Since",
                table: "ImportRuns");

            migrationBuilder.DropColumn(
                name: "TransientFailures",
                table: "ImportRuns");
        }
    }
}
