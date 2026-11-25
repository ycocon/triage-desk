using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TriageDesk.Api.Http;
using TriageDesk.Api.Modules.IdentityAndWorkspaces;
using TriageDesk.Api.Modules.RepositoriesAndImport;
using TriageDesk.Api.Persistence;

namespace TriageDesk.Api.Modules.TriageAndActivity;

internal static class IssueHistory
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    internal static void MapWorkspaceActivity(this RouteGroupBuilder api) =>
        api.MapGet("/workspaces/{workspaceId:guid}/activity", WorkspaceActivity).RequireAuthorization();

    internal static async Task<IResult> List(Guid workspaceId, Guid issueId, HttpContext context,
        WorkspaceAccess access, TriageDeskDbContext db, CancellationToken ct)
    {
        if (await access.ReadAsync(workspaceId, WorkspaceAccess.Caller(context), ct) is null ||
            !await db.IssueTriage.AnyAsync(t => t.WorkspaceId == workspaceId && t.IssueId == issueId, ct))
            return WorkspaceAccess.Missing(context);
        if (!Workspaces.ReadPage(context, out var page, out var size)) return ApiProblem.Invalid(context);

        var rows = Issues.Rows(db, workspaceId).Where(row => row.Snapshot.Id == issueId);
        return await Page(db, workspaceId, rows, page, size, ct);
    }

    private static async Task<IResult> WorkspaceActivity(Guid workspaceId, HttpContext context,
        WorkspaceAccess access, TriageDeskDbContext db, CancellationToken ct)
    {
        if (await access.ReadAsync(workspaceId, WorkspaceAccess.Caller(context), ct) is null)
            return WorkspaceAccess.Missing(context);
        if (!Workspaces.ReadPage(context, out var page, out var size, "repositoryId"))
            return ApiProblem.Invalid(context);

        var rows = Issues.Rows(db, workspaceId);
        if (context.Request.Query.TryGetValue("repositoryId", out var filter))
        {
            if (!Guid.TryParseExact(filter[0], "D", out var repositoryId)) return ApiProblem.Invalid(context);
            rows = rows.Where(row => row.Repository.Id == repositoryId);
        }
        return await Page(db, workspaceId, rows, page, size, ct);
    }

    private static async Task<IResult> Page(TriageDeskDbContext db, Guid workspaceId,
        IQueryable<Issues.IssueRow> rows, int page, int size, CancellationToken ct)
    {
        var entries = from activity in db.IssueActivity.AsNoTracking()
                      join issue in rows on activity.IssueId equals issue.Snapshot.Id
                      where activity.WorkspaceId == workspaceId
                      select new { Activity = activity, SourceAvailable = issue.Snapshot.SourceAvailable &&
                          issue.Repository.Public && issue.Repository.DisconnectedAt == null };
        await using var read = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var total = await entries.CountAsync(ct);
        var offset = (long)(page - 1) * size;
        var items = offset >= total ? [] : await entries.OrderByDescending(row => row.Activity.OccurredAt)
            .ThenByDescending(row => row.Activity.Id)
            .Skip((int)offset).Take(size)
            .Select(row => new { View = new ActivityView(row.Activity.Id, row.Activity.IssueId, row.Activity.ActorId,
                db.Users.Where(u => u.Id == row.Activity.ActorId).Select(u => u.DisplayName).SingleOrDefault(),
                row.Activity.ActorId == null ? "system" : "user", row.Activity.Kind, row.Activity.OccurredAt,
                row.Activity.Changes, row.Activity.Reason), row.SourceAvailable })
            .ToArrayAsync(ct);
        await read.CommitAsync(ct);
        return Results.Ok(new { items = items.Select(row => row.SourceAvailable ? row.View : RedactSource(row.View)),
            page, pageSize = size, total });
    }

    private static ActivityView RedactSource(ActivityView entry) => entry with
    {
        Changes = JsonSerializer.SerializeToElement(entry.Changes.EnumerateArray().Where(change =>
            change.GetProperty("field").GetString() is "status" or "priority" or "severity" or "assigneeId"
                or "nextStep" or "question" or "answer" or "deferralReason" or "sourceReviewFlag").ToArray(), WebJson)
    };

    internal static void Record(TriageDeskDbContext db, IssueTriage triage, Guid? actor, Guid operationId,
        string kind, DateTimeOffset time, IReadOnlyList<ActivityChange> changes, string? digest = null, string? reason = null)
    {
        triage.UpdatedAt = time;
        triage.DecisionVersion++;
        db.IssueActivity.Add(new IssueActivity
        {
            WorkspaceId = triage.WorkspaceId, IssueId = triage.IssueId, Id = Guid.CreateVersion7(),
            ActorId = actor, OperationId = operationId, CommandDigest = digest, Kind = kind,
            OccurredAt = time, Changes = JsonSerializer.SerializeToElement(changes, WebJson), Reason = reason
        });
    }

    internal static async Task ReturnRetiredAssignments(TriageDeskDbContext db, Guid workspaceId,
        Guid assignee, Guid actor, DateTimeOffset now, CancellationToken ct)
    {
        var active = await db.IssueTriage.Where(t => t.WorkspaceId == workspaceId && t.AssigneeId == assignee &&
            (t.Status == "ready" || t.Status == "inProgress" || t.Status == "waitingForInformation")).ToListAsync(ct);
        var operationId = Guid.NewGuid();
        foreach (var triage in active)
        {
            var changes = new List<ActivityChange>(3);
            triage.Status = IssueDecisions.Track(changes, "status", triage.Status, "needsReview");
            triage.AssigneeId = IssueDecisions.Track(changes, "assigneeId", triage.AssigneeId, null);
            triage.Question = IssueDecisions.Track(changes, "question", triage.Question, null);
            triage.EnteredReviewAt = now;
            Record(db, triage, actor, operationId, "assignmentReturned", now, changes,
                reason: "The assignee left the workspace.");
        }
    }
}
