using Microsoft.EntityFrameworkCore;
using TriageDesk.Api.Modules.IdentityAndWorkspaces;
using TriageDesk.Api.Modules.RepositoriesAndImport;
using TriageDesk.Api.Modules.TriageAndActivity;

namespace TriageDesk.Api.Persistence;

internal static class ImportModel
{
    internal static void ConfigureImport(this ModelBuilder model)
    {
        model.Entity<RepositoryConnection>(entity =>
        {
            entity.HasKey(r => new { r.WorkspaceId, r.Id });
            entity.HasIndex(r => new { r.WorkspaceId, r.GitHubId }).IsUnique();
            entity.HasIndex(r => new { r.WorkspaceId, r.FullName, r.Id });
            entity.Property(r => r.FullName).HasMaxLength(140);
            entity.HasOne<Workspace>().WithMany().HasForeignKey(r => r.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<ImportRun>(entity =>
        {
            entity.HasKey(r => new { r.WorkspaceId, r.Id });
            entity.HasIndex(r => new { r.WorkspaceId, r.RepositoryId }).IsUnique()
                .HasFilter(""" "EndedAt" IS NULL """);
            entity.HasOne<RepositoryConnection>().WithMany()
                .HasForeignKey(r => new { r.WorkspaceId, Id = r.RepositoryId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<WorkspaceMember>().WithMany()
                .HasForeignKey(r => new { r.WorkspaceId, UserId = r.InitiatorId }).OnDelete(DeleteBehavior.Restrict);
            entity.Property(r => r.Mode).HasMaxLength(11);
            entity.Property(r => r.State).HasMaxLength(10);
            entity.Property(r => r.LastErrorCode).HasMaxLength(40);
        });
        model.Entity<IssueSnapshot>(entity =>
        {
            entity.HasKey(i => new { i.WorkspaceId, i.Id });
            entity.HasIndex(i => new { i.WorkspaceId, i.GitHubId }).IsUnique();
            entity.HasIndex(i => new { i.WorkspaceId, i.RepositoryId, i.Number });
            entity.HasOne<RepositoryConnection>().WithMany()
                .HasForeignKey(i => new { i.WorkspaceId, Id = i.RepositoryId }).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<IssueTriage>(entity =>
        {
            entity.HasKey(t => new { t.WorkspaceId, t.IssueId });
            entity.HasIndex(t => new { t.WorkspaceId, t.Status, t.EnteredReviewAt, t.IssueId });
            entity.HasOne<IssueSnapshot>().WithOne()
                .HasForeignKey<IssueTriage>(t => new { t.WorkspaceId, Id = t.IssueId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<WorkspaceMember>().WithMany()
                .HasForeignKey(t => new { t.WorkspaceId, UserId = t.AssigneeId }).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<IssueActivity>(entity =>
        {
            entity.HasKey(a => new { a.WorkspaceId, a.Id });
            entity.HasIndex(a => new { a.WorkspaceId, a.IssueId, a.OperationId, a.Ordinal }).IsUnique();
            entity.HasIndex(a => new { a.WorkspaceId, a.IssueId, a.OccurredAt, a.Id }).IsDescending(false, false, true, true);
            entity.Property(a => a.Kind).HasMaxLength(36);
            entity.Property(a => a.CommandDigest).HasMaxLength(64);
            entity.Property(a => a.Changes).HasColumnType("jsonb");
            entity.HasOne<IssueSnapshot>().WithMany()
                .HasForeignKey(a => new { a.WorkspaceId, Id = a.IssueId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<WorkspaceMember>().WithMany()
                .HasForeignKey(a => new { a.WorkspaceId, UserId = a.ActorId }).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
