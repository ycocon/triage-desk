using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TriageDesk.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class IssueHandoff : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "IssueActivity",
                columns: table => new
                {
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IssueId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: true),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    CommandDigest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Kind = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Changes = table.Column<JsonElement>(type: "jsonb", nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IssueActivity", x => new { x.WorkspaceId, x.Id });
                    table.ForeignKey(
                        name: "FK_IssueActivity_IssueSnapshots_WorkspaceId_IssueId",
                        columns: x => new { x.WorkspaceId, x.IssueId },
                        principalTable: "IssueSnapshots",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_IssueActivity_WorkspaceMembers_WorkspaceId_ActorId",
                        columns: x => new { x.WorkspaceId, x.ActorId },
                        principalTable: "WorkspaceMembers",
                        principalColumns: new[] { "WorkspaceId", "UserId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IssueActivity_WorkspaceId_ActorId",
                table: "IssueActivity",
                columns: new[] { "WorkspaceId", "ActorId" });

            migrationBuilder.CreateIndex(
                name: "IX_IssueActivity_WorkspaceId_IssueId_OccurredAt_Id",
                table: "IssueActivity",
                columns: new[] { "WorkspaceId", "IssueId", "OccurredAt", "Id" },
                descending: new[] { false, false, true, true });

            migrationBuilder.CreateIndex(
                name: "IX_IssueActivity_WorkspaceId_IssueId_OperationId_Ordinal",
                table: "IssueActivity",
                columns: new[] { "WorkspaceId", "IssueId", "OperationId", "Ordinal" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IssueActivity");
        }
    }
}
