using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using TriageDesk.Api.Modules.IdentityAndWorkspaces;
using TriageDesk.Api.Modules.RepositoriesAndImport;
using TriageDesk.Api.Modules.TriageAndActivity;
using TriageDesk.Api.Operations;

namespace TriageDesk.Api.Persistence;

public sealed class TriageDeskDbContext(DbContextOptions<TriageDeskDbContext> options)
    : IdentityUserContext<Account, Guid>(options)
{
    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<WorkspaceMember> WorkspaceMembers => Set<WorkspaceMember>();
    public DbSet<WorkspaceInvitation> Invitations => Set<WorkspaceInvitation>();
    public DbSet<TeamAuditEntry> TeamAudit => Set<TeamAuditEntry>();
    public DbSet<RepositoryConnection> Repositories => Set<RepositoryConnection>();
    public DbSet<ImportRun> ImportRuns => Set<ImportRun>();
    public DbSet<IssueSnapshot> IssueSnapshots => Set<IssueSnapshot>();
    public DbSet<IssueExternalKey> IssueExternalKeys => Set<IssueExternalKey>();
    public DbSet<SourceAccessLoss> SourceAccessLosses => Set<SourceAccessLoss>();
    public DbSet<IssueTriage> IssueTriage => Set<IssueTriage>();
    public DbSet<IssueActivity> IssueActivity => Set<IssueActivity>();
    public DbSet<ProtectionKeyRecord> ProtectionKeys => Set<ProtectionKeyRecord>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        base.OnModelCreating(model);
        model.ConfigureImport();
        model.Entity<Account>(entity =>
        {
            entity.Property(a => a.DisplayName).HasMaxLength(80);
            entity.Property(a => a.Theme).HasMaxLength(6);
            entity.ToTable(table => table.HasCheckConstraint("CK_Accounts_Theme",
                """ "Theme" IN ('system', 'light', 'dark') """));
        });
        model.Entity<Workspace>(entity =>
        {
            entity.Property(w => w.Name).HasMaxLength(80);
            entity.ToTable(table => table.HasCheckConstraint("CK_Workspaces_Name",
                """ length(btrim("Name")) > 0 """));
        });
        model.Entity<WorkspaceMember>(entity =>
        {
            entity.HasKey(m => new { m.WorkspaceId, m.UserId });
            entity.Property(m => m.BaseRole).HasMaxLength(8);
            entity.HasOne<Workspace>().WithMany().HasForeignKey(m => m.WorkspaceId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Account>().WithMany().HasForeignKey(m => m.UserId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.ToTable(table => table.HasCheckConstraint("CK_WorkspaceMembers_BaseRole",
                """ "BaseRole" IN ('member', 'reviewer') """));
        });
        model.Entity<WorkspaceInvitation>(entity =>
        {
            entity.HasKey(i => new { i.WorkspaceId, i.Id });
            entity.Property(i => i.Email).HasMaxLength(254);
            entity.Property(i => i.NormalizedEmail).HasMaxLength(256);
            entity.Property(i => i.Role).HasMaxLength(8);
            entity.Property(i => i.TokenDigest).HasMaxLength(64);
            entity.Property(i => i.State).HasMaxLength(8);
            entity.HasIndex(i => i.TokenDigest).IsUnique();
            entity.HasIndex(i => new { i.WorkspaceId, i.NormalizedEmail }).IsUnique()
                .HasFilter(""" "State" = 'pending' """);
            entity.HasIndex(i => new { i.WorkspaceId, i.IssuedAt, i.Id }).IsDescending(false, true, false);
            entity.HasOne<WorkspaceMember>().WithMany().HasForeignKey(i => new { i.WorkspaceId, UserId = i.InviterUserId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<WorkspaceMember>().WithMany().HasForeignKey(i => new { i.WorkspaceId, UserId = i.AcceptedByUserId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.ToTable(table =>
            {
                table.HasCheckConstraint("CK_Invitations_Role", """ "Role" IN ('member', 'reviewer') """);
                table.HasCheckConstraint("CK_Invitations_State",
                    """
                    ("State" = 'pending' AND "ResolvedAt" IS NULL AND "AcceptedByUserId" IS NULL)
                    OR ("State" IN ('expired', 'revoked') AND "ResolvedAt" IS NOT NULL AND "AcceptedByUserId" IS NULL)
                    OR ("State" = 'accepted' AND "ResolvedAt" IS NOT NULL AND "AcceptedByUserId" IS NOT NULL)
                    """);
                table.HasCheckConstraint("CK_Invitations_Expiry", """ "ExpiresAt" > "IssuedAt" """);
            });
        });
        model.Entity<TeamAuditEntry>(entity =>
        {
            entity.Property(a => a.Action).HasMaxLength(32);
            entity.Property(a => a.PreviousRole).HasMaxLength(8);
            entity.Property(a => a.NextRole).HasMaxLength(8);
            entity.HasIndex(a => new { a.WorkspaceId, a.OccurredAt });
            entity.HasOne<WorkspaceMember>().WithMany().HasForeignKey(a => new { a.WorkspaceId, UserId = a.ActorUserId })
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
