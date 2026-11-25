using Microsoft.EntityFrameworkCore;
using TriageDesk.Api.Http;
using TriageDesk.Api.Modules.IdentityAndWorkspaces;
using TriageDesk.Api.Modules.TriageAndActivity;
using TriageDesk.Api.Persistence;

namespace TriageDesk.Api.Modules.RepositoriesAndImport;

internal static class Issues
{
    private sealed class IssueRow
    {
        public required IssueSnapshot Snapshot { get; init; }
        public required RepositoryConnection Repository { get; init; }
        public required IssueTriage Triage { get; init; }
    }

    internal static void MapIssues(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/workspaces/{workspaceId:guid}/issues").RequireAuthorization();
        group.MapGet("", List);
        group.MapGet("/{issueId:guid}", Detail);
    }

    private static IQueryable<IssueRow> Rows(TriageDeskDbContext db, Guid workspaceId) =>
        from issue in db.IssueSnapshots.AsNoTracking()
        join repo in db.Repositories.AsNoTracking()
            on new { issue.WorkspaceId, Id = issue.RepositoryId } equals new { repo.WorkspaceId, repo.Id }
        join triage in db.IssueTriage.AsNoTracking()
            on new { issue.WorkspaceId, IssueId = issue.Id } equals new { triage.WorkspaceId, triage.IssueId }
        where issue.WorkspaceId == workspaceId
        select new IssueRow { Snapshot = issue, Repository = repo, Triage = triage };

    private static async Task<IResult> List(Guid workspaceId, HttpContext context, WorkspaceAccess access,
        TriageDeskDbContext db, CancellationToken ct)
    {
        var actor = WorkspaceAccess.Caller(context);
        if (await access.ReadAsync(workspaceId, actor, ct) is null)
            return WorkspaceAccess.Missing(context);
        if (!Workspaces.ReadPage(context, out var page, out var size, "view", "repositoryId", "status", "githubState"))
            return ApiProblem.Invalid(context);

        var query = context.Request.Query;
        var view = query.TryGetValue("view", out var filter) ? filter[0] : "all";
        if (view is not ("all" or "needsReview" or "available" or "myWork" or "activeWork")) return ApiProblem.Invalid(context);

        var visible = Rows(db, workspaceId).Where(row =>
            row.Snapshot.SourceAvailable && row.Repository.Public && row.Repository.DisconnectedAt == null);
        visible = view switch
        {
            "needsReview" => visible.Where(row => row.Triage.Status == "needsReview"),
            "available" => visible.Where(row => row.Triage.Status == "ready" && row.Triage.AssigneeId == null),
            "myWork" or "activeWork" => visible.Where(row => row.Triage.Status == "ready" ||
                row.Triage.Status == "inProgress" || row.Triage.Status == "waitingForInformation"),
            _ => visible
        };
        if (view == "myWork") visible = visible.Where(row => row.Triage.AssigneeId == actor);
        if (query.TryGetValue("repositoryId", out var repository))
        {
            if (!Guid.TryParse(repository[0], out var repositoryId)) return ApiProblem.Invalid(context);
            visible = visible.Where(row => row.Repository.Id == repositoryId);
        }
        if (query.TryGetValue("status", out var workflow))
        {
            var status = workflow[0];
            if (status is not ("needsReview" or "ready" or "waitingForInformation" or "inProgress" or "done" or "setAside"))
                return ApiProblem.Invalid(context);
            visible = visible.Where(row => row.Triage.Status == status);
        }
        if (query.TryGetValue("githubState", out var source))
        {
            var state = source[0];
            if (state is not ("open" or "closed")) return ApiProblem.Invalid(context);
            visible = visible.Where(row => row.Snapshot.State == state);
        }

        var total = await visible.CountAsync(ct);
        var offset = (long)(page - 1) * size;
        var order = view switch
        {
            "needsReview" => visible.OrderBy(row => row.Triage.EnteredReviewAt),
            "available" => visible.OrderBy(row => row.Triage.Priority == "high" ? 0 :
                row.Triage.Priority == "normal" ? 1 : row.Triage.Priority == "low" ? 2 : 3),
            _ => visible.OrderByDescending(row => row.Triage.UpdatedAt)
        };
        // List reads project the source summary in SQL. Leave full issue bodies for detail.
        var rows = offset >= total ? [] : await order.ThenBy(row => row.Snapshot.Id)
            .Skip((int)offset).Take(size).Select(row => new IssueRow
            {
                Snapshot = new IssueSnapshot
                {
                    Id = row.Snapshot.Id, WorkspaceId = row.Snapshot.WorkspaceId, RepositoryId = row.Snapshot.RepositoryId,
                    GitHubId = row.Snapshot.GitHubId, Number = row.Snapshot.Number, Title = row.Snapshot.Title,
                    State = row.Snapshot.State, Labels = row.Snapshot.Labels, SourceUpdatedAt = row.Snapshot.SourceUpdatedAt,
                    ObservedAt = row.Snapshot.ObservedAt, SourceAvailable = true
                },
                Repository = new RepositoryConnection { FullName = row.Repository.FullName },
                Triage = row.Triage
            }).ToArrayAsync(ct);
        return Results.Ok(new { items = rows.Select(row => View(row, true, false)), page, pageSize = size, total });
    }

    private static async Task<IResult> Detail(Guid workspaceId, Guid issueId, HttpContext context,
        WorkspaceAccess access, TriageDeskDbContext db, CancellationToken ct)
    {
        if (await access.ReadAsync(workspaceId, WorkspaceAccess.Caller(context), ct) is null)
            return WorkspaceAccess.Missing(context);

        var row = await Rows(db, workspaceId).SingleOrDefaultAsync(row => row.Snapshot.Id == issueId, ct);
        if (row is null) return WorkspaceAccess.Missing(context);
        var available = row.Snapshot.SourceAvailable && row.Repository.Public && row.Repository.DisconnectedAt == null;
        return Results.Ok(View(row, available, true));
    }

    private static object View(IssueRow row, bool available, bool detail)
    {
        var issue = row.Snapshot;
        var triage = row.Triage;
        var url = $"https://github.com/{row.Repository.FullName}/issues/{issue.Number}";
        var id = Repositories.Decimal(issue.GitHubId);
        object? source = !available ? null : detail
            ? new { githubIssueId = id, issue.Number, issue.Title, issue.State, issue.Labels, url,
                issue.SourceUpdatedAt, issue.ObservedAt, issue.Body, issue.SourceCreatedAt }
            : new { githubIssueId = id, issue.Number, issue.Title, issue.State, issue.Labels, url,
                issue.SourceUpdatedAt, issue.ObservedAt };
        return new
        {
            issue.WorkspaceId, issueId = issue.Id, issue.RepositoryId,
            sourceAvailability = available ? "available" : "unavailable", source,
            triage = IssueDecisions.View(triage)
        };
    }
}
