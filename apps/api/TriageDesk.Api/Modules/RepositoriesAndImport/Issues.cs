using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using TriageDesk.Api.Http;
using TriageDesk.Api.Modules.IdentityAndWorkspaces;
using TriageDesk.Api.Modules.TriageAndActivity;
using TriageDesk.Api.Persistence;

namespace TriageDesk.Api.Modules.RepositoriesAndImport;

internal static partial class Issues
{
    internal sealed class IssueRow
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

    internal static IQueryable<IssueRow> Rows(TriageDeskDbContext db, Guid workspaceId) =>
        from issue in db.IssueSnapshots.AsNoTracking()
        join repo in db.Repositories.AsNoTracking()
            on new { issue.WorkspaceId, Id = issue.RepositoryId } equals new { repo.WorkspaceId, repo.Id }
        join triage in db.IssueTriage.AsNoTracking()
            on new { issue.WorkspaceId, IssueId = issue.Id } equals new { triage.WorkspaceId, triage.IssueId }
        where issue.WorkspaceId == workspaceId
        select new IssueRow { Snapshot = issue, Repository = repo, Triage = triage };

    internal static IQueryable<IssueRow> Visible(TriageDeskDbContext db, Guid workspaceId) =>
        Rows(db, workspaceId).Where(row => row.Snapshot.SourceAvailable && row.Repository.Public &&
            row.Repository.DisconnectedAt == null);

    internal static IQueryable<IssueRow> Queue(IQueryable<IssueRow> visible, string view, Guid actor = default) => view switch
    {
        "needsReview" => visible.Where(row => row.Triage.Status == "needsReview"),
        "available" => visible.Where(row => row.Triage.Status == "ready" && row.Triage.AssigneeId == null),
        "sourceChanges" => visible.Where(row => row.Triage.SourceReviewFlag != null),
        "myWork" => Queue(visible, "activeWork").Where(row => row.Triage.AssigneeId == actor),
        "activeWork" => visible.Where(row => row.Triage.Status == "ready" ||
            row.Triage.Status == "inProgress" || row.Triage.Status == "waitingForInformation"),
        _ => visible
    };

    private static async Task<IResult> List(Guid workspaceId, HttpContext context, WorkspaceAccess access,
        TriageDeskDbContext db, CancellationToken ct)
    {
        var actor = WorkspaceAccess.Caller(context);
        if (await access.ReadAsync(workspaceId, actor, ct) is null)
            return WorkspaceAccess.Missing(context);
        if (!Workspaces.ReadPage(context, out var page, out var size, "view", "repositoryId", "status", "githubState",
            "priority", "assigneeId", "q", "sort", "direction"))
            return ApiProblem.Invalid(context);

        var query = context.Request.Query;
        var view = query.TryGetValue("view", out var filter) ? filter[0] : "all";
        if (view is not ("all" or "needsReview" or "available" or "myWork" or "activeWork" or "sourceChanges"))
            return ApiProblem.Invalid(context);

        var visible = Queue(Visible(db, workspaceId), view, actor);
        if (query.TryGetValue("repositoryId", out var repository))
        {
            if (!Guid.TryParseExact(repository[0], "D", out var repositoryId)) return ApiProblem.Invalid(context);
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
        if (query.TryGetValue("priority", out var priority))
        {
            var value = priority[0];
            if (value is not ("high" or "normal" or "low")) return ApiProblem.Invalid(context);
            visible = visible.Where(row => row.Triage.Priority == value);
        }
        if (query.TryGetValue("assigneeId", out var assignee))
        {
            if (assignee[0] == "unassigned")
                visible = visible.Where(row => row.Triage.AssigneeId == null);
            else if (Guid.TryParseExact(assignee[0], "D", out var assigneeId))
                visible = visible.Where(row => row.Triage.AssigneeId == assigneeId);
            else return ApiProblem.Invalid(context);
        }
        if (query.TryGetValue("q", out var search))
        {
            var term = search[0]?.Trim();
            if (term is null || term.Length is < 1 or > 160 || term.Contains('\0')) return ApiProblem.Invalid(context);
            visible = Search(visible, term);
        }

        var sort = query.TryGetValue("sort", out var sortBy) ? sortBy[0] : view switch
        {
            "needsReview" => "enteredReviewAt",
            "available" => "priority",
            _ => "updatedAt"
        };
        var direction = query.TryGetValue("direction", out var orderBy) ? orderBy[0] :
            sort == "updatedAt" ? "desc" : "asc";
        if (sort is not ("enteredReviewAt" or "priority" or "updatedAt" or "number") || direction is not ("asc" or "desc"))
            return ApiProblem.Invalid(context);

        await using var read = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var total = await visible.CountAsync(ct);
        var offset = (long)(page - 1) * size;
        // List reads project the source summary in SQL. Leave full issue bodies for detail.
        var rows = offset >= total ? [] : await Ordered(visible, sort, direction == "desc").ThenBy(row => row.Snapshot.Id)
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
        await read.CommitAsync(ct);
        return Results.Ok(new { items = rows.Select(row => View(row, true, false)), page, pageSize = size, total });
    }

    [GeneratedRegex(@"^([A-Za-z0-9_.-]+(?:/[A-Za-z0-9_.-]+)?)\s*#([0-9]+)$", RegexOptions.CultureInvariant)]
    private static partial Regex QualifiedNumber();

    private static IQueryable<IssueRow> Search(IQueryable<IssueRow> rows, string term)
    {
        var qualified = QualifiedNumber().Match(term);
        if (!qualified.Success)
        {
            var pattern = "%" + LikeLiteral(term) + "%";
            return rows.Where(row => EF.Functions.ILike(row.Snapshot.Title, pattern, "\\"));
        }
        if (!int.TryParse(qualified.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0)
            return rows.Where(_ => false);
        var slug = qualified.Groups[1].Value;
        var repository = (slug.Contains('/') ? "" : "%/") + LikeLiteral(slug);
        return rows.Where(row => row.Snapshot.Number == number && EF.Functions.ILike(row.Repository.FullName, repository, "\\"));
    }

    private static string LikeLiteral(string term) =>
        term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    internal static IOrderedQueryable<IssueRow> Ordered(IQueryable<IssueRow> rows, string sort, bool descending) =>
        (sort, descending) switch
        {
            ("enteredReviewAt", false) => rows.OrderBy(row => row.Triage.EnteredReviewAt == null).ThenBy(row => row.Triage.EnteredReviewAt),
            ("enteredReviewAt", true) => rows.OrderBy(row => row.Triage.EnteredReviewAt == null).ThenByDescending(row => row.Triage.EnteredReviewAt),
            ("priority", false) => rows.OrderBy(row => row.Triage.Priority == null)
                .ThenBy(row => row.Triage.Priority == "high" ? 0 : row.Triage.Priority == "normal" ? 1 : 2),
            ("priority", true) => rows.OrderBy(row => row.Triage.Priority == null)
                .ThenByDescending(row => row.Triage.Priority == "high" ? 0 : row.Triage.Priority == "normal" ? 1 : 2),
            ("number", false) => rows.OrderBy(row => row.Snapshot.Number),
            ("number", true) => rows.OrderByDescending(row => row.Snapshot.Number),
            (_, false) => rows.OrderBy(row => row.Triage.UpdatedAt),
            _ => rows.OrderByDescending(row => row.Triage.UpdatedAt)
        };

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
