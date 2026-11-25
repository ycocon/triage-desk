using Microsoft.EntityFrameworkCore;
using TriageDesk.Api.Http;
using TriageDesk.Api.Modules.IdentityAndWorkspaces;
using TriageDesk.Api.Modules.TriageAndActivity;
using TriageDesk.Api.Persistence;

namespace TriageDesk.Api.Modules.RepositoriesAndImport;

internal sealed record StartImport(Guid RunId);
internal sealed record AdvanceImport(int? ExpectedCheckpoint);
internal sealed record RunView(Guid RunId, Guid RepositoryId, string Mode, string State, int Checkpoint, int Examined,
    int Imported, int Updated, string? LastErrorCode, DateTimeOffset? RetryAt, DateTimeOffset StartedAt, DateTimeOffset? EndedAt);

internal static class Imports
{
    private sealed record Lease(Guid Id, long GitHubId, string FullName, int Page);

    internal static void MapImports(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/workspaces/{workspaceId:guid}/repositories/{repositoryId:guid}/sync-runs")
            .RequireAuthorization();
        group.MapPost("", Start);
        group.MapGet("/{runId:guid}", Get);
        group.MapPost("/{runId:guid}/advance", Advance);
        group.MapPost("/{runId:guid}/pause", Pause);
    }

    private static Task<IResult> Start(Guid workspaceId, Guid repositoryId, StartImport request, HttpContext context,
        WorkspaceAccess access, TriageDeskDbContext db, TimeProvider clock, CancellationToken ct) =>
        Write(workspaceId, repositoryId, context, access, db, async repo =>
        {
            if (request.RunId == Guid.Empty) return ApiProblem.Invalid(context);
            var prior = await db.ImportRuns.FindAsync([workspaceId, request.RunId], ct);
            if (prior is not null)
                return prior.RepositoryId == repositoryId ? Results.Ok(View(prior)) :
                    WorkspaceAccess.Conflict(context, "run_id_reused", "This refresh identifier is already in use.");
            var openId = await db.ImportRuns.Where(r => r.WorkspaceId == workspaceId &&
                r.RepositoryId == repositoryId && r.EndedAt == null).Select(r => (Guid?)r.Id).SingleOrDefaultAsync(ct);
            if (openId is { } existing)
                return Results.Problem(statusCode: 409, type: "urn:triagedesk:problem:open_run_exists",
                    title: "A refresh is already open.", extensions: new Dictionary<string, object?>
                    { ["code"] = "open_run_exists", ["runId"] = existing,
                        ["traceId"] = System.Diagnostics.Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier });

            var now = clock.GetUtcNow();
            var run = new ImportRun
            {
                WorkspaceId = workspaceId, Id = request.RunId, RepositoryId = repositoryId,
                InitiatorId = WorkspaceAccess.Caller(context), Mode = repo.LastSuccessAt is null ? "initial" : "incremental",
                StartedAt = now, UpdatedAt = now
            };
            db.ImportRuns.Add(run);
            return Results.Created($"/api/v1/workspaces/{workspaceId}/repositories/{repositoryId}/sync-runs/{run.Id}", View(run));
        }, ct);

    private static async Task<IResult> Get(Guid workspaceId, Guid repositoryId, Guid runId, HttpContext context,
        WorkspaceAccess access, TriageDeskDbContext db, CancellationToken ct)
    {
        var actor = WorkspaceAccess.Caller(context);
        var workspace = await access.ReadAsync(workspaceId, actor, ct);
        var repo = workspace is null ? null : await db.Repositories.AsNoTracking()
            .SingleOrDefaultAsync(r => r.WorkspaceId == workspaceId && r.Id == repositoryId, ct);
        if (repo is null) return WorkspaceAccess.Missing(context);
        if (repo.DisconnectedAt is not null && workspace!.OwnerUserId != actor) return WorkspaceAccess.Forbidden(context);
        var run = await db.ImportRuns.AsNoTracking()
            .SingleOrDefaultAsync(r => r.WorkspaceId == workspaceId && r.RepositoryId == repositoryId && r.Id == runId, ct);
        return run is null ? WorkspaceAccess.Missing(context) : Results.Ok(View(run));
    }

    private static Task<IResult> Pause(Guid workspaceId, Guid repositoryId, Guid runId, AdvanceImport request, HttpContext context,
        WorkspaceAccess access, TriageDeskDbContext db, TimeProvider clock, CancellationToken ct) =>
        Write(workspaceId, repositoryId, context, access, db, async _ =>
        {
            var run = await Find(db, workspaceId, repositoryId, runId, ct);
            if (run is null) return WorkspaceAccess.Missing(context);
            if (Guard(run, request, context) is { } earlier) return earlier;
            var now = clock.GetUtcNow();
            if (run.LeaseUntil > now) return Busy(context, run.LeaseUntil.Value - now);
            run.State = "paused";
            run.UpdatedAt = now;
            ClearLease(run);
            return Results.Ok(View(run));
        }, ct);

    private static async Task<IResult> Advance(Guid workspaceId, Guid repositoryId, Guid runId, AdvanceImport request,
        HttpContext context, WorkspaceAccess access, TriageDeskDbContext db, GitHubIssuesClient github,
        TimeProvider clock, CancellationToken ct)
    {
        Lease? lease = null;
        var claimed = await Write(workspaceId, repositoryId, context, access, db, async repo =>
        {
            var run = await Find(db, workspaceId, repositoryId, runId, ct);
            if (run is null) return WorkspaceAccess.Missing(context);
            if (Guard(run, request, context) is { } earlier) return earlier;
            var now = clock.GetUtcNow();
            if (run.LeaseUntil > now) return Busy(context, run.LeaseUntil.Value - now);
            if (run.RetryAt > now) return new GitHubFailure(429, "rate_limited", run.RetryAt.Value - now).Result(context);
            lease = new Lease(Guid.NewGuid(), repo.GitHubId, repo.FullName, run.NextPage);
            run.LeaseId = lease.Id;
            run.LeaseUntil = now.AddSeconds(40);
            run.State = "running";
            run.UpdatedAt = now;
            return Results.NoContent();
        }, ct);
        if (lease is null) return claimed;

        // Query the public source without holding any database locks.
        GitHubFailure? failure = null;
        GitHubRepository? upstream = null;
        GitHubPage? page = null;
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                upstream = await github.GetRepository(lease.FullName, deadline.Token);
                if (upstream.Id != lease.GitHubId) throw GitHubIssuesClient.Missing();
                page = await github.GetPage(upstream, lease.Page, deadline.Token);
            }
            catch (GitHubFailure ex) { failure = ex; }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { failure = GitHubIssuesClient.Upstream(); }
        }

        // The first transaction left tracked entities behind. A different request may have
        // changed access or cancelled the run while the network call was in flight.
        db.ChangeTracker.Clear();
        var savedFailure = false;
        var result = await Write(workspaceId, repositoryId, context, access, db, async repo =>
        {
            var run = await Find(db, workspaceId, repositoryId, runId, ct);
            if (run is null) return WorkspaceAccess.Missing(context);
            var now = clock.GetUtcNow();
            if (run.EndedAt is not null || run.Checkpoint > request.ExpectedCheckpoint)
                return Results.Ok(View(run));
            if (run.LeaseId != lease.Id || run.LeaseUntil <= now)
                return Busy(context, TimeSpan.FromSeconds(1));
            ClearLease(run);
            run.UpdatedAt = now;

            if (failure is not null)
            {
                savedFailure = true;
                run.LastErrorCode = failure.Status == 404 ? "source_unavailable" : failure.Code;
                run.RetryAt = failure.Delay is { } delay ? now + delay : null;
                if (failure.Status == 404)
                {
                    repo.Public = false;
                    await Repositories.HideSnapshots(db, repo, scrub: true, ct);
                    End(run, "failed", now);
                }
                else run.State = "paused";
                return Results.Ok(View(run));
            }

            repo.FullName = upstream!.FullName;
            repo.Public = true;
            await SavePage(db, repo, run, page!, now, ct);
            return Results.Ok(View(run));
        }, ct);
        return savedFailure && failure is not null
            ? (failure.Status == 404 ? GitHubIssuesClient.Upstream() : failure).Result(context)
            : result;
    }

    private static async Task SavePage(TriageDeskDbContext db, RepositoryConnection repo, ImportRun run,
        GitHubPage page, DateTimeOffset now, CancellationToken ct)
    {
        var workspaceId = repo.WorkspaceId;
        var incoming = page.Issues.GroupBy(i => i.Id).Select(group => group.MaxBy(i => i.UpdatedAt)!).ToArray();
        var keys = incoming.Select(i => i.Id).ToArray();
        var existing = await db.IssueSnapshots.Where(i => i.WorkspaceId == workspaceId && keys.Contains(i.GitHubId))
            .ToDictionaryAsync(i => i.GitHubId, ct);

        foreach (var issue in incoming)
        {
            if (!existing.TryGetValue(issue.Id, out var snapshot))
            {
                snapshot = new IssueSnapshot
                {
                    WorkspaceId = workspaceId, Id = Guid.CreateVersion7(), RepositoryId = repo.Id, GitHubId = issue.Id
                };
                db.IssueSnapshots.Add(snapshot);
                db.IssueTriage.Add(new IssueTriage
                {
                    WorkspaceId = workspaceId, IssueId = snapshot.Id, EnteredReviewAt = now, UpdatedAt = now
                });
                run.Imported++;
            }
            else
            {
                // Preserve another connection's report until transfer reconciliation can verify identity.
                if (snapshot.RepositoryId != repo.Id || issue.UpdatedAt < snapshot.SourceUpdatedAt) continue;
                if (!snapshot.SourceAvailable || snapshot.SourceUpdatedAt < issue.UpdatedAt ||
                    snapshot.Number != issue.Number || snapshot.Title != issue.Title || snapshot.Body != issue.Body ||
                    snapshot.State != issue.State || !snapshot.Labels.SequenceEqual(issue.Labels))
                    run.Updated++;
            }

            snapshot.Number = issue.Number;
            snapshot.Title = issue.Title;
            snapshot.Body = issue.Body;
            snapshot.State = issue.State;
            snapshot.Labels = issue.Labels;
            snapshot.SourceCreatedAt = issue.CreatedAt;
            snapshot.SourceUpdatedAt = issue.UpdatedAt;
            snapshot.ObservedAt = now;
            snapshot.SourceAvailable = true;
        }
        run.Examined += page.Examined;
        run.Checkpoint++;
        run.LastErrorCode = null;
        run.RetryAt = null;
        if (page.NextPage is { } next)
        {
            run.NextPage = next;
            run.State = "running";
        }
        else
        {
            End(run, "succeeded", now);
            repo.LastSuccessAt = now;
            repo.LastSuccessWatermark = run.StartedAt;
        }
    }

    private static async Task<ImportRun?> Find(TriageDeskDbContext db, Guid workspaceId, Guid repositoryId, Guid runId,
        CancellationToken ct)
    {
        var run = await db.ImportRuns.FindAsync([workspaceId, runId], ct);
        return run?.RepositoryId == repositoryId ? run : null;
    }

    private static IResult? Guard(ImportRun run, AdvanceImport request, HttpContext context)
    {
        if (request.ExpectedCheckpoint is null or < 0) return ApiProblem.Invalid(context);
        if (run.EndedAt is not null || request.ExpectedCheckpoint < run.Checkpoint) return Results.Ok(View(run));
        if (request.ExpectedCheckpoint > run.Checkpoint)
            return WorkspaceAccess.Conflict(context, "invalid_checkpoint", "Reload the saved refresh progress.");
        return null;
    }

    private static Task<IResult> Write(Guid workspaceId, Guid repositoryId, HttpContext context, WorkspaceAccess access,
        TriageDeskDbContext db, Func<RepositoryConnection, Task<IResult>> execute, CancellationToken ct) =>
        access.ChangeAsync(workspaceId, context, ownerOnly: false, async workspace =>
        {
            var actor = WorkspaceAccess.Caller(context);
            if (workspace.OwnerUserId != actor && !await db.WorkspaceMembers.AsNoTracking().AnyAsync(m =>
                m.WorkspaceId == workspaceId && m.UserId == actor && m.RemovedAt == null && m.BaseRole == "reviewer", ct))
                return ApiProblem.Result(context, 403, "forbidden", "Only an Owner or Reviewer can refresh this repository.");

            var repo = await db.Repositories.FindAsync([workspaceId, repositoryId], ct);
            if (repo is null) return WorkspaceAccess.Missing(context);
            if (repo.DisconnectedAt is not null)
                return WorkspaceAccess.Conflict(context, "repository_disconnected", "Reconnect this repository before refreshing.");
            return await execute(repo);
        }, ct);

    private static IResult Busy(HttpContext context, TimeSpan delay)
    {
        context.Response.Headers.RetryAfter = Math.Ceiling(delay.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return WorkspaceAccess.Conflict(context, "run_busy", "A refresh batch is already running. Try again shortly.");
    }

    internal static void End(ImportRun run, string state, DateTimeOffset now)
    {
        run.State = state;
        run.EndedAt = run.UpdatedAt = now;
        run.RetryAt = null;
        ClearLease(run);
    }

    private static void ClearLease(ImportRun run)
    {
        run.LeaseId = null;
        run.LeaseUntil = null;
    }

    private static RunView View(ImportRun run) =>
        new(run.Id, run.RepositoryId, run.Mode, run.State, run.Checkpoint, run.Examined, run.Imported, run.Updated,
            run.LastErrorCode, run.RetryAt, run.StartedAt, run.EndedAt);
}
