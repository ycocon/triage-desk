using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TriageDesk.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WorkspaceTeamAndInvitations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Invitations",
                columns: table => new
                {
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InviterUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AcceptedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    NormalizedEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Role = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    TokenDigest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    State = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    IssuedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Invitations", x => new { x.WorkspaceId, x.Id });
                    table.CheckConstraint("CK_Invitations_Expiry", " \"ExpiresAt\" > \"IssuedAt\" ");
                    table.CheckConstraint("CK_Invitations_Role", " \"Role\" IN ('member', 'reviewer') ");
                    table.CheckConstraint("CK_Invitations_State", "(\"State\" = 'pending' AND \"ResolvedAt\" IS NULL AND \"AcceptedByUserId\" IS NULL)\nOR (\"State\" IN ('expired', 'revoked') AND \"ResolvedAt\" IS NOT NULL AND \"AcceptedByUserId\" IS NULL)\nOR (\"State\" = 'accepted' AND \"ResolvedAt\" IS NOT NULL AND \"AcceptedByUserId\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_Invitations_WorkspaceMembers_WorkspaceId_AcceptedByUserId",
                        columns: x => new { x.WorkspaceId, x.AcceptedByUserId },
                        principalTable: "WorkspaceMembers",
                        principalColumns: new[] { "WorkspaceId", "UserId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Invitations_WorkspaceMembers_WorkspaceId_InviterUserId",
                        columns: x => new { x.WorkspaceId, x.InviterUserId },
                        principalTable: "WorkspaceMembers",
                        principalColumns: new[] { "WorkspaceId", "UserId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TeamAudit",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubjectUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    InvitationId = table.Column<Guid>(type: "uuid", nullable: true),
                    Action = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PreviousRole = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    NextRole = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TeamAudit", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TeamAudit_WorkspaceMembers_WorkspaceId_ActorUserId",
                        columns: x => new { x.WorkspaceId, x.ActorUserId },
                        principalTable: "WorkspaceMembers",
                        principalColumns: new[] { "WorkspaceId", "UserId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Invitations_TokenDigest",
                table: "Invitations",
                column: "TokenDigest",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Invitations_WorkspaceId_AcceptedByUserId",
                table: "Invitations",
                columns: new[] { "WorkspaceId", "AcceptedByUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_Invitations_WorkspaceId_InviterUserId",
                table: "Invitations",
                columns: new[] { "WorkspaceId", "InviterUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_Invitations_WorkspaceId_IssuedAt_Id",
                table: "Invitations",
                columns: new[] { "WorkspaceId", "IssuedAt", "Id" },
                descending: new[] { false, true, false });

            migrationBuilder.CreateIndex(
                name: "IX_Invitations_WorkspaceId_NormalizedEmail",
                table: "Invitations",
                columns: new[] { "WorkspaceId", "NormalizedEmail" },
                unique: true,
                filter: " \"State\" = 'pending' ");

            migrationBuilder.CreateIndex(
                name: "IX_TeamAudit_WorkspaceId_ActorUserId",
                table: "TeamAudit",
                columns: new[] { "WorkspaceId", "ActorUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_TeamAudit_WorkspaceId_OccurredAt",
                table: "TeamAudit",
                columns: new[] { "WorkspaceId", "OccurredAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Invitations");

            migrationBuilder.DropTable(
                name: "TeamAudit");
        }
    }
}
