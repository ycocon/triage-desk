using Microsoft.EntityFrameworkCore;
using TriageDesk.Api.Persistence;

namespace TriageDesk.Api.Modules.RepositoriesAndImport;

internal static class SourceVisibility
{
    // Cross-workspace source updates share a transaction lock. Upstream calls run outside this lock.
    internal static Task Lock(TriageDeskDbContext db, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(1953655143, 1)", ct);

    internal static Task<bool> HasNewerLoss(TriageDeskDbContext db, long repoId, DateTimeOffset readStartedAt,
        GitHubPage? page, IReadOnlyList<RecheckedIssue>? rechecks, CancellationToken ct)
    {
        var repoIds = (rechecks ?? []).Where(result => result.Report is not null)
            .Select(result => result.Report!.Repository.Id).Append(repoId).Distinct().ToArray();
        var issueIds = (page?.Issues.Select(issue => issue.Id) ?? Enumerable.Empty<long>())
            .Concat((rechecks ?? []).SelectMany(result => result.Report is { } report
                ? new[] { result.Target.GitHubId, report.Issue.Id } : [result.Target.GitHubId]))
            .Distinct().ToArray();
        return db.SourceAccessLosses.AnyAsync(loss => loss.DetectedAt > readStartedAt &&
            repoIds.Contains(loss.RepositoryGitHubId) && (loss.IssueGitHubId == 0 || issueIds.Contains(loss.IssueGitHubId)), ct);
    }

    internal static async Task HideRepository(TriageDeskDbContext db, RepositoryConnection origin, ImportRun ending,
        DateTimeOffset now, CancellationToken ct)
    {
        await RecordLoss(db, origin.GitHubId, 0, now, ct);
        var copies = db.Repositories.Where(repo => repo.GitHubId == origin.GitHubId);
        await Scrub(db.IssueSnapshots.Where(issue => copies.Any(repo => repo.WorkspaceId == issue.WorkspaceId &&
            repo.Id == issue.RepositoryId)), now, ct);
        await copies.ExecuteUpdateAsync(setters => setters.SetProperty(repo => repo.Public, false)
            .SetProperty(repo => repo.RequiresFullScan, true), ct);
        await db.ImportRuns.Where(run => run.EndedAt == null &&
            (run.Id != ending.Id || run.WorkspaceId != ending.WorkspaceId) &&
            copies.Any(repo => repo.WorkspaceId == run.WorkspaceId && repo.Id == run.RepositoryId))
            .ExecuteUpdateAsync(setters => setters.SetProperty(run => run.State, "failed")
                .SetProperty(run => run.LastErrorCode, "source_unavailable")
                .SetProperty(run => run.EndedAt, (DateTimeOffset?)now).SetProperty(run => run.UpdatedAt, now)
                .SetProperty(run => run.LeaseId, (Guid?)null).SetProperty(run => run.LeaseUntil, (DateTimeOffset?)null)
                .SetProperty(run => run.RetryAt, (DateTimeOffset?)null), ct);
    }

    internal static async Task HideIssue(TriageDeskDbContext db, long repoId, long issueId, DateTimeOffset now,
        CancellationToken ct)
    {
        await RecordLoss(db, repoId, issueId, now, ct);
        await Scrub(db.IssueSnapshots.Where(issue => issue.GitHubId == issueId &&
            db.Repositories.Any(repo => repo.WorkspaceId == issue.WorkspaceId && repo.Id == issue.RepositoryId &&
                repo.GitHubId == repoId)), now, ct);
    }

    private static Task<int> Scrub(IQueryable<IssueSnapshot> copies, DateTimeOffset now, CancellationToken ct) =>
        copies.ExecuteUpdateAsync(setters => setters.SetProperty(issue => issue.SourceAvailable, false)
            .SetProperty(issue => issue.Title, "").SetProperty(issue => issue.Body, (string?)null)
            .SetProperty(issue => issue.Labels, Array.Empty<string>()).SetProperty(issue => issue.NeedsRecheck, false)
            .SetProperty(issue => issue.ObservedAt, now), ct);

    private static Task<int> RecordLoss(TriageDeskDbContext db, long repoId, long issueId, DateTimeOffset now,
        CancellationToken ct) => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "SourceAccessLosses" ("RepositoryGitHubId", "IssueGitHubId", "DetectedAt")
            VALUES ({repoId}, {issueId}, {now})
            ON CONFLICT ("RepositoryGitHubId", "IssueGitHubId") DO UPDATE
            SET "DetectedAt" = GREATEST("SourceAccessLosses"."DetectedAt", EXCLUDED."DetectedAt")
            """, ct);
}
