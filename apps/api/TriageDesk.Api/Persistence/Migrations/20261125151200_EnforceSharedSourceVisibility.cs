using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TriageDesk.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EnforceSharedSourceVisibility : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SourceAccessLosses",
                columns: table => new
                {
                    RepositoryGitHubId = table.Column<long>(type: "bigint", nullable: false),
                    IssueGitHubId = table.Column<long>(type: "bigint", nullable: false),
                    DetectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceAccessLosses", x => new { x.RepositoryGitHubId, x.IssueGitHubId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_Repositories_GitHubId",
                table: "Repositories",
                column: "GitHubId");

            migrationBuilder.CreateIndex(
                name: "IX_IssueSnapshots_GitHubId",
                table: "IssueSnapshots",
                column: "GitHubId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SourceAccessLosses");

            migrationBuilder.DropIndex(
                name: "IX_Repositories_GitHubId",
                table: "Repositories");

            migrationBuilder.DropIndex(
                name: "IX_IssueSnapshots_GitHubId",
                table: "IssueSnapshots");
        }
    }
}
