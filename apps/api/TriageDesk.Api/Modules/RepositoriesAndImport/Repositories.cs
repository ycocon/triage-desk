using System.Globalization;
using Microsoft.EntityFrameworkCore;
using TriageDesk.Api.Http;
using TriageDesk.Api.Modules.IdentityAndWorkspaces;
using TriageDesk.Api.Persistence;

namespace TriageDesk.Api.Modules.RepositoriesAndImport;

internal sealed record RepositoryInput(string? FullName);
internal sealed record RepositoryView(Guid RepositoryId, string GitHubRepositoryId, string FullName,
    string Connection, string Visibility, DateTimeOffset? LastSuccessAt, Guid? OpenRunId);

internal static class Repositories
{
    internal static void MapRepositories(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/workspaces/{workspaceId:guid}").RequireAuthorization();
        group.MapPost("/repository-previews", Preview);
        group.MapGet("/repositories", List);
        group.MapPost("/repositories", Connect);
        group.MapGet("/repositories/{repositoryId:guid}", Detail);
        group.MapDelete("/repositories/{repositoryId:guid}", Disconnect);
    }

    private static async Task<IResult> Preview(Guid workspaceId, RepositoryInput request, HttpContext context,
        WorkspaceAccess access, GitHubIssuesClient github, CancellationToken ct)
    {
        if (await OwnerAccess(workspaceId, context, access, ct) is { } denied) return denied;
        if (!GitHubIssuesClient.ValidName(request.FullName)) return ApiProblem.Invalid(context);
        try
        {
            var repo = await github.GetRepository(request.FullName!, ct);
            return Results.Ok(new { githubRepositoryId = Decimal(repo.Id), repo.FullName, visibility = "public", repo.WebUrl });
        }
        catch (GitHubFailure failure) { return failure.Result(context); }
    }

    private static async Task<IResult> Connect(Guid workspaceId, RepositoryInput request, HttpContext context,
        WorkspaceAccess access, TriageDeskDbContext db, GitHubIssuesClient github, TimeProvider clock, CancellationToken ct)
    {
        if (await OwnerAccess(workspaceId, context, access, ct) is { } denied) return denied;
        if (!GitHubIssuesClient.ValidName(request.FullName)) return ApiProblem.Invalid(context);
        GitHubRepository upstream;
        var readStartedAt = clock.GetUtcNow();
        try { upstream = await github.GetRepository(request.FullName!, ct); }
        catch (GitHubFailure failure) { return failure.Result(context); }

        return await access.ChangeAsync(workspaceId, context, ownerOnly: true, async _ =>
        {
            await SourceVisibility.Lock(db, ct);
            if (await SourceVisibility.HasNewerLoss(db, upstream.Id, readStartedAt, null, null, ct))
                return GitHubIssuesClient.Upstream().Result(context);

            var repo = await db.Repositories.SingleOrDefaultAsync(r =>
                r.WorkspaceId == workspaceId && r.GitHubId == upstream.Id, ct);
            if (repo is not null)
            {
                if (repo.DisconnectedAt is not null) repo.ConnectedAt = clock.GetUtcNow();
                repo.FullName = upstream.FullName;
                repo.Public = true;
                repo.DisconnectedAt = null;
                return Results.Ok(View(repo, await OpenRun(db, workspaceId, repo.Id, ct)));
            }

            repo = new RepositoryConnection
            {
                Id = Guid.CreateVersion7(), WorkspaceId = workspaceId, GitHubId = upstream.Id,
                FullName = upstream.FullName, ConnectedAt = clock.GetUtcNow()
            };
            db.Repositories.Add(repo);
            return Results.Created($"/api/v1/workspaces/{workspaceId}/repositories/{repo.Id}", View(repo, null));
        }, ct);
    }

    private static async Task<IResult> List(Guid workspaceId, HttpContext context, WorkspaceAccess access,
        TriageDeskDbContext db, CancellationToken ct)
    {
        var actor = WorkspaceAccess.Caller(context);
        if (await access.ReadAsync(workspaceId, actor, ct) is not { } workspace)
            return WorkspaceAccess.Missing(context);
        if (!Workspaces.ReadPage(context, out var page, out var size, "connection")) return ApiProblem.Invalid(context);
        var view = context.Request.Query.TryGetValue("connection", out var value) ? value[0] : "connected";
        if (view is not ("connected" or "disconnected" or "all")) return ApiProblem.Invalid(context);
        if (view != "connected" && workspace.OwnerUserId != actor) return WorkspaceAccess.Forbidden(context);

        var repos = db.Repositories.AsNoTracking().Where(r => r.WorkspaceId == workspaceId);
        if (view == "connected") repos = repos.Where(r => r.DisconnectedAt == null);
        if (view == "disconnected") repos = repos.Where(r => r.DisconnectedAt != null);
        var total = await repos.CountAsync(ct);
        var offset = (long)(page - 1) * size;
        var rows = offset >= total ? [] : await repos.OrderBy(r => r.FullName).ThenBy(r => r.Id)
            .Skip((int)offset).Take(size).ToArrayAsync(ct);
        var ids = rows.Select(r => r.Id).ToArray();
        var runs = rows.Length == 0 ? new Dictionary<Guid, Guid>() : await db.ImportRuns.AsNoTracking()
            .Where(r => r.WorkspaceId == workspaceId && ids.Contains(r.RepositoryId) && r.EndedAt == null)
            .ToDictionaryAsync(r => r.RepositoryId, r => r.Id, ct);
        var items = rows.Select(repo => View(repo, runs.TryGetValue(repo.Id, out var run) ? run : null));
        return Results.Ok(new { items, page, pageSize = size, total });
    }

    private static async Task<IResult> Detail(Guid workspaceId, Guid repositoryId, HttpContext context,
        WorkspaceAccess access, TriageDeskDbContext db, CancellationToken ct)
    {
        var actor = WorkspaceAccess.Caller(context);
        var workspace = await access.ReadAsync(workspaceId, actor, ct);
        var repo = workspace is null ? null : await db.Repositories.AsNoTracking()
            .SingleOrDefaultAsync(r => r.WorkspaceId == workspaceId && r.Id == repositoryId, ct);
        if (repo is null) return WorkspaceAccess.Missing(context);
        if (repo.DisconnectedAt is not null && workspace!.OwnerUserId != actor) return WorkspaceAccess.Forbidden(context);
        return Results.Ok(View(repo, await OpenRun(db, workspaceId, repo.Id, ct)));
    }

    private static Task<IResult> Disconnect(Guid workspaceId, Guid repositoryId, HttpContext context,
        WorkspaceAccess access, TriageDeskDbContext db, TimeProvider clock, CancellationToken ct) =>
        access.ChangeAsync(workspaceId, context, ownerOnly: true, async _ =>
        {
            await SourceVisibility.Lock(db, ct);
            var repo = await db.Repositories.FindAsync([workspaceId, repositoryId], ct);
            if (repo is null) return WorkspaceAccess.Missing(context);
            if (repo.DisconnectedAt is not null) return Results.NoContent();
            var now = clock.GetUtcNow();
            repo.DisconnectedAt = now;
            repo.RequiresFullScan = true;
            await db.IssueSnapshots.Where(i => i.WorkspaceId == workspaceId && i.RepositoryId == repo.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(issue => issue.SourceAvailable, false), ct);
            var run = await db.ImportRuns.SingleOrDefaultAsync(r =>
                r.WorkspaceId == workspaceId && r.RepositoryId == repositoryId && r.EndedAt == null, ct);
            if (run is not null) Imports.End(run, "cancelled", now);
            return Results.NoContent();
        }, ct);

    internal static string Decimal(long id) => id.ToString(CultureInfo.InvariantCulture);

    private static Task<Guid?> OpenRun(TriageDeskDbContext db, Guid workspaceId, Guid repoId, CancellationToken ct) =>
        db.ImportRuns.Where(r => r.WorkspaceId == workspaceId && r.RepositoryId == repoId && r.EndedAt == null)
            .Select(r => (Guid?)r.Id).SingleOrDefaultAsync(ct);

    private static RepositoryView View(RepositoryConnection repo, Guid? runId) =>
        new(repo.Id, Decimal(repo.GitHubId), repo.FullName, repo.DisconnectedAt is null ? "connected" : "disconnected",
            repo.Public ? "public" : "unavailable", repo.LastSuccessAt, runId);

    private static async Task<IResult?> OwnerAccess(Guid workspaceId, HttpContext context, WorkspaceAccess access, CancellationToken ct)
    {
        var actor = WorkspaceAccess.Caller(context);
        var workspace = await access.ReadAsync(workspaceId, actor, ct);
        return workspace is null ? WorkspaceAccess.Missing(context) :
            workspace.OwnerUserId != actor ? WorkspaceAccess.Forbidden(context) : null;
    }
}
