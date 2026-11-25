using Microsoft.EntityFrameworkCore;
using TriageDesk.Api.Modules.TriageAndActivity;
using TriageDesk.Api.Persistence;

namespace TriageDesk.Api.Modules.RepositoriesAndImport;

internal sealed record RecheckTarget(Guid Id, long GitHubId, int Number);
internal sealed record RecheckedIssue(RecheckTarget Target, GitHubCanonicalIssue? Report);

internal static class ImportReconciliation
{
    private const int RechecksPerBatch = 3;

    private static IQueryable<IssueSnapshot> Candidates(TriageDeskDbContext db, RepositoryConnection repo, ImportRun run)
    {
        var query = db.IssueSnapshots.AsNoTracking().Where(i => i.WorkspaceId == repo.WorkspaceId &&
            i.RepositoryId == repo.Id && (i.NeedsRecheck || i.ObservedAt < run.StartedAt));
        if (run.RecheckAfter is { } processed)
            query = query.Where(i => i.Id.CompareTo(processed) > 0);
        return query;
    }

    internal static async Task<bool> HasTargets(TriageDeskDbContext db, RepositoryConnection repo, ImportRun run,
        CancellationToken ct)
    {
        // The last listing page is still tracked inside this transaction.
        var observed = db.ChangeTracker.Entries<IssueSnapshot>().Select(entry => entry.Entity)
            .Where(i => !i.NeedsRecheck && i.ObservedAt >= run.StartedAt).Select(i => i.Id).ToArray();
        var query = Candidates(db, repo, run);
        if (observed.Length > 0) query = query.Where(i => !observed.Contains(i.Id));
        return await query.AnyAsync(ct) || db.ChangeTracker.Entries<IssueSnapshot>().Any(entry =>
            entry.Entity.RepositoryId == repo.Id && entry.Entity.NeedsRecheck &&
            (run.RecheckAfter is not { } after || entry.Entity.Id.CompareTo(after) > 0));
    }

    internal static async Task<IReadOnlyList<RecheckTarget>> Targets(TriageDeskDbContext db, RepositoryConnection repo,
        ImportRun run, CancellationToken ct) =>
        await Candidates(db, repo, run).OrderBy(i => i.Id).Take(RechecksPerBatch)
            .Select(i => new RecheckTarget(i.Id, i.GitHubId, i.Number)).ToArrayAsync(ct);

    internal static async Task<IReadOnlyList<RecheckedIssue>> ReadTargets(GitHubIssuesClient github,
        GitHubRepository upstream, IReadOnlyList<RecheckTarget> targets, CancellationToken ct)
    {
        var results = new List<RecheckedIssue>(targets.Count);
        foreach (var target in targets)
        {
            try { results.Add(new RecheckedIssue(target, await github.Recheck(upstream, target.Number, ct))); }
            catch (GitHubFailure failure) when (failure.Status == 404) { results.Add(new RecheckedIssue(target, null)); }
        }
        return results;
    }

    internal static async Task ApplyPage(TriageDeskDbContext db, RepositoryConnection repo, ImportRun run,
        GitHubPage page, DateTimeOffset now, CancellationToken ct)
    {
        var workspaceId = repo.WorkspaceId;
        var incoming = page.Issues.Where(i => i.UpdatedAt <= run.StartedAt && (run.Since is null || i.UpdatedAt >= run.Since))
            .GroupBy(i => i.Id).Select(group => group.MaxBy(i => i.UpdatedAt)!).ToArray();
        var keys = incoming.Select(i => i.Id).ToArray();
        var numbers = incoming.Select(i => i.Number).ToArray();
        var existing = await db.IssueSnapshots.Where(i => i.WorkspaceId == workspaceId &&
            (keys.Contains(i.GitHubId) || i.RepositoryId == repo.Id && numbers.Contains(i.Number))).ToArrayAsync(ct);
        var byKey = existing.ToDictionary(i => i.GitHubId);
        var byNumber = existing.Where(i => i.RepositoryId == repo.Id).ToLookup(i => i.Number);
        var retired = await db.IssueExternalKeys.Where(k => k.WorkspaceId == workspaceId &&
            keys.Contains(k.GitHubId) && !k.Current).Select(k => k.GitHubId).ToListAsync(ct);

        foreach (var issue in incoming)
        {
            if (retired.Contains(issue.Id)) continue;
            if (byKey.TryGetValue(issue.Id, out var snapshot))
            {
                if (snapshot.RepositoryId != repo.Id)
                {
                    snapshot.NeedsRecheck = true;
                    continue;
                }
                var changed = await ApplyObservation(db, snapshot, issue, run, now, canonical: false, transfer: null, ct);
                if (changed) run.Updated++;
                continue;
            }
            if (byNumber[issue.Number].Any())
            {
                foreach (var prior in byNumber[issue.Number]) prior.NeedsRecheck = true;
                continue;
            }
            if (issue.State == "closed") continue;

            snapshot = new IssueSnapshot
            {
                WorkspaceId = workspaceId, Id = Guid.CreateVersion7(), RepositoryId = repo.Id, GitHubId = issue.Id
            };
            Copy(snapshot, issue, now);
            db.IssueSnapshots.Add(snapshot);
            db.IssueExternalKeys.Add(Key(snapshot, issue.Id, now, current: true));
            db.IssueTriage.Add(new IssueTriage
            {
                WorkspaceId = workspaceId, IssueId = snapshot.Id, EnteredReviewAt = now, UpdatedAt = now
            });
            run.Imported++;
        }
    }

    internal static async Task ApplyRechecks(TriageDeskDbContext db, RepositoryConnection original, ImportRun run,
        IReadOnlyList<RecheckedIssue> results, DateTimeOffset now, CancellationToken ct)
    {
        foreach (var result in results)
        {
            var snapshot = await db.IssueSnapshots.FindAsync([original.WorkspaceId, result.Target.Id], ct);
            if (snapshot is null || snapshot.RepositoryId != original.Id || snapshot.GitHubId != result.Target.GitHubId ||
                snapshot.Number != result.Target.Number) continue;
            if (result.Report is not { } report)
            {
                await SourceVisibility.HideIssue(db, original.GitHubId, snapshot.GitHubId, now, ct);
                if (Mask(snapshot, now)) run.Updated++;
                continue;
            }
            if (report.Repository.Id == original.GitHubId)
            {
                if (report.Issue.Id != snapshot.GitHubId)
                {
                    await SourceVisibility.HideIssue(db, original.GitHubId, snapshot.GitHubId, now, ct);
                    if (Mask(snapshot, now)) run.Updated++;
                    continue;
                }
                if (await ApplyObservation(db, snapshot, report.Issue, run, now, canonical: true, transfer: null, ct))
                    run.Updated++;
                continue;
            }

            var destination = await db.Repositories.SingleOrDefaultAsync(r => r.WorkspaceId == original.WorkspaceId &&
                r.GitHubId == report.Repository.Id && r.DisconnectedAt == null, ct);
            if (destination is null)
            {
                if (Mask(snapshot, now)) run.Updated++;
                continue;
            }
            if (report.Issue.UpdatedAt > run.StartedAt)
            {
                // Its activity belongs to the next run's cutoff window.
                snapshot.NeedsRecheck = true;
                continue;
            }

            var external = await db.IssueExternalKeys.FindAsync([original.WorkspaceId, report.Issue.Id], ct);
            var aliasIssueId = external?.IssueId;
            var occupied = await db.IssueSnapshots.Where(i => i.WorkspaceId == original.WorkspaceId && i.Id != snapshot.Id &&
                (i.GitHubId == report.Issue.Id || i.RepositoryId == destination.Id && i.Number == report.Issue.Number ||
                 i.Id == aliasIssueId)).ToListAsync(ct);
            if (occupied.Count > 0)
            {
                await FlagConflict(db, snapshot, now, ct);
                foreach (var issue in occupied) await FlagConflict(db, issue, now, ct);
                snapshot.ObservedAt = now;
                snapshot.NeedsRecheck = false;
                continue;
            }

            destination.FullName = report.Repository.FullName;
            destination.Public = true;
            if (report.Issue.Id != snapshot.GitHubId)
            {
                await db.IssueExternalKeys.Where(k => k.WorkspaceId == original.WorkspaceId &&
                    k.IssueId == snapshot.Id && k.Current)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(k => k.Current, false), ct);
                if (external is null) db.IssueExternalKeys.Add(Key(snapshot, report.Issue.Id, now, current: true));
                else external.Current = true;
                snapshot.GitHubId = report.Issue.Id;
            }
            if (await ApplyObservation(db, snapshot, report.Issue, run, now, canonical: true, transfer: destination.Id, ct))
                run.Updated++;
        }
    }

    private static async Task<bool> ApplyObservation(TriageDeskDbContext db, IssueSnapshot snapshot, GitHubIssue source,
        ImportRun run, DateTimeOffset now, bool canonical, Guid? transfer, CancellationToken ct)
    {
        if (source.UpdatedAt > run.StartedAt || (transfer is null && source.UpdatedAt < snapshot.SourceUpdatedAt))
        {
            if (canonical)
            {
                snapshot.ObservedAt = now;
                snapshot.NeedsRecheck = true;
            }
            return false;
        }

        var contentChanged = snapshot.Number != source.Number || snapshot.Title != source.Title ||
            snapshot.Body != source.Body || snapshot.State != source.State || !snapshot.Labels.SequenceEqual(source.Labels);
        if (!canonical && snapshot.SourceAvailable && source.UpdatedAt == snapshot.SourceUpdatedAt && contentChanged)
        {
            snapshot.NeedsRecheck = true;
            return false;
        }

        var changes = new List<ActivityChange>(3);
        if (transfer is { } destination)
        {
            snapshot.RepositoryId = IssueDecisions.Track(changes, "repositoryId", snapshot.RepositoryId, destination);
            if (snapshot.Number != source.Number) changes.Add(new("number", snapshot.Number, source.Number));
        }
        if (changes.Count > 0 || snapshot.State != source.State)
        {
            var triage = (await db.IssueTriage.FindAsync([snapshot.WorkspaceId, snapshot.Id], ct))!;
            var flag = ReviewFlag(triage, snapshot.State, source.State);
            triage.SourceReviewFlag = IssueDecisions.Track(changes, "sourceReviewFlag", triage.SourceReviewFlag, flag);
            if (changes.Count > 0)
                IssueHistory.Record(db, triage, null, Guid.NewGuid(), transfer is null ? "sourceStateChanged" : "sourceTransferred",
                    now, changes);
        }

        var changed = contentChanged || transfer is not null || !snapshot.SourceAvailable || snapshot.SourceUpdatedAt != source.UpdatedAt;
        Copy(snapshot, source, now);
        return changed;
    }

    private static string? ReviewFlag(IssueTriage triage, string before, string after)
    {
        if (before == after || triage.SourceReviewFlag == "transferConflict") return triage.SourceReviewFlag;
        if (after == "closed" && triage.Status is "needsReview" or "ready" or "inProgress" or "waitingForInformation")
            return "closedWhileActive";
        if (after == "open" && triage.Status is "done" or "setAside") return "reopenedAfterDecision";
        return triage.SourceReviewFlag;
    }

    private static async Task FlagConflict(TriageDeskDbContext db, IssueSnapshot snapshot,
        DateTimeOffset now, CancellationToken ct)
    {
        var triage = (await db.IssueTriage.FindAsync([snapshot.WorkspaceId, snapshot.Id], ct))!;
        if (triage.SourceReviewFlag == "transferConflict") return;
        var prior = triage.SourceReviewFlag;
        triage.SourceReviewFlag = "transferConflict";
        IssueHistory.Record(db, triage, null, Guid.NewGuid(), "sourceTransferConflict", now,
            [new("sourceReviewFlag", prior, triage.SourceReviewFlag)]);
    }

    private static IssueExternalKey Key(IssueSnapshot snapshot, long id, DateTimeOffset now, bool current) =>
        new() { WorkspaceId = snapshot.WorkspaceId, IssueId = snapshot.Id, GitHubId = id, Current = current, FirstSeenAt = now };

    private static bool Mask(IssueSnapshot snapshot, DateTimeOffset now)
    {
        var previouslyVisible = snapshot.SourceAvailable;
        snapshot.SourceAvailable = false;
        snapshot.NeedsRecheck = false;
        snapshot.Title = "";
        snapshot.Body = null;
        snapshot.Labels = [];
        snapshot.ObservedAt = now;
        return previouslyVisible;
    }

    private static void Copy(IssueSnapshot snapshot, GitHubIssue source, DateTimeOffset observed)
    {
        snapshot.Number = source.Number;
        snapshot.Title = source.Title;
        snapshot.Body = source.Body;
        snapshot.State = source.State;
        snapshot.Labels = source.Labels;
        snapshot.SourceCreatedAt = source.CreatedAt;
        snapshot.SourceUpdatedAt = source.UpdatedAt;
        snapshot.ObservedAt = observed;
        snapshot.SourceAvailable = true;
        snapshot.NeedsRecheck = false;
    }
}
