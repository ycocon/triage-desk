using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TriageDesk.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PublicRepositoryImport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Repositories",
                columns: table => new
                {
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GitHubId = table.Column<long>(type: "bigint", nullable: false),
                    FullName = table.Column<string>(type: "character varying(140)", maxLength: 140, nullable: false),
                    ConnectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DisconnectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Public = table.Column<bool>(type: "boolean", nullable: false),
                    LastSuccessAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastSuccessWatermark = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Repositories", x => new { x.WorkspaceId, x.Id });
                    table.ForeignKey(
                        name: "FK_Repositories_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ImportRuns",
                columns: table => new
                {
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RepositoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    InitiatorId = table.Column<Guid>(type: "uuid", nullable: false),
                    Mode = table.Column<string>(type: "character varying(11)", maxLength: 11, nullable: false),
                    State = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Checkpoint = table.Column<int>(type: "integer", nullable: false),
                    NextPage = table.Column<int>(type: "integer", nullable: false),
                    Examined = table.Column<int>(type: "integer", nullable: false),
                    Imported = table.Column<int>(type: "integer", nullable: false),
                    Updated = table.Column<int>(type: "integer", nullable: false),
                    LeaseId = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastErrorCode = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    RetryAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EndedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportRuns", x => new { x.WorkspaceId, x.Id });
                    table.ForeignKey(
                        name: "FK_ImportRuns_Repositories_WorkspaceId_RepositoryId",
                        columns: x => new { x.WorkspaceId, x.RepositoryId },
                        principalTable: "Repositories",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ImportRuns_WorkspaceMembers_WorkspaceId_InitiatorId",
                        columns: x => new { x.WorkspaceId, x.InitiatorId },
                        principalTable: "WorkspaceMembers",
                        principalColumns: new[] { "WorkspaceId", "UserId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "IssueSnapshots",
                columns: table => new
                {
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RepositoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    GitHubId = table.Column<long>(type: "bigint", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    Body = table.Column<string>(type: "text", nullable: true),
                    State = table.Column<string>(type: "text", nullable: false),
                    Labels = table.Column<string[]>(type: "text[]", nullable: false),
                    SourceAvailable = table.Column<bool>(type: "boolean", nullable: false),
                    SourceCreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SourceUpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ObservedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IssueSnapshots", x => new { x.WorkspaceId, x.Id });
                    table.ForeignKey(
                        name: "FK_IssueSnapshots_Repositories_WorkspaceId_RepositoryId",
                        columns: x => new { x.WorkspaceId, x.RepositoryId },
                        principalTable: "Repositories",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "IssueTriage",
                columns: table => new
                {
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    IssueId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    Priority = table.Column<string>(type: "text", nullable: true),
                    Severity = table.Column<string>(type: "text", nullable: true),
                    AssigneeId = table.Column<Guid>(type: "uuid", nullable: true),
                    NextStep = table.Column<string>(type: "text", nullable: true),
                    Question = table.Column<string>(type: "text", nullable: true),
                    DeferralReason = table.Column<string>(type: "text", nullable: true),
                    SourceReviewFlag = table.Column<string>(type: "text", nullable: true),
                    EnteredReviewAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DecisionVersion = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IssueTriage", x => new { x.WorkspaceId, x.IssueId });
                    table.ForeignKey(
                        name: "FK_IssueTriage_IssueSnapshots_WorkspaceId_IssueId",
                        columns: x => new { x.WorkspaceId, x.IssueId },
                        principalTable: "IssueSnapshots",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_IssueTriage_WorkspaceMembers_WorkspaceId_AssigneeId",
                        columns: x => new { x.WorkspaceId, x.AssigneeId },
                        principalTable: "WorkspaceMembers",
                        principalColumns: new[] { "WorkspaceId", "UserId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ImportRuns_WorkspaceId_InitiatorId",
                table: "ImportRuns",
                columns: new[] { "WorkspaceId", "InitiatorId" });

            migrationBuilder.CreateIndex(
                name: "IX_ImportRuns_WorkspaceId_RepositoryId",
                table: "ImportRuns",
                columns: new[] { "WorkspaceId", "RepositoryId" },
                unique: true,
                filter: " \"EndedAt\" IS NULL ");

            migrationBuilder.CreateIndex(
                name: "IX_IssueSnapshots_WorkspaceId_GitHubId",
                table: "IssueSnapshots",
                columns: new[] { "WorkspaceId", "GitHubId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IssueSnapshots_WorkspaceId_RepositoryId_Number",
                table: "IssueSnapshots",
                columns: new[] { "WorkspaceId", "RepositoryId", "Number" });

            migrationBuilder.CreateIndex(
                name: "IX_IssueTriage_WorkspaceId_AssigneeId",
                table: "IssueTriage",
                columns: new[] { "WorkspaceId", "AssigneeId" });

            migrationBuilder.CreateIndex(
                name: "IX_IssueTriage_WorkspaceId_Status_EnteredReviewAt_IssueId",
                table: "IssueTriage",
                columns: new[] { "WorkspaceId", "Status", "EnteredReviewAt", "IssueId" });

            migrationBuilder.CreateIndex(
                name: "IX_Repositories_WorkspaceId_FullName_Id",
                table: "Repositories",
                columns: new[] { "WorkspaceId", "FullName", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Repositories_WorkspaceId_GitHubId",
                table: "Repositories",
                columns: new[] { "WorkspaceId", "GitHubId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ImportRuns");

            migrationBuilder.DropTable(
                name: "IssueTriage");

            migrationBuilder.DropTable(
                name: "IssueSnapshots");

            migrationBuilder.DropTable(
                name: "Repositories");
        }
    }
}
