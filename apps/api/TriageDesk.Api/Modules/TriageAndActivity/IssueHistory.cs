using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TriageDesk.Api.Http;
using TriageDesk.Api.Modules.IdentityAndWorkspaces;
using TriageDesk.Api.Persistence;

namespace TriageDesk.Api.Modules.TriageAndActivity;

internal static class IssueHistory
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    internal static async Task<IResult> List(Guid workspaceId, Guid issueId, HttpContext context,
        WorkspaceAccess access, TriageDeskDbContext db, CancellationToken ct)
    {
        if (await access.ReadAsync(workspaceId, WorkspaceAccess.Caller(context), ct) is null ||
            !await db.IssueTriage.AnyAsync(t => t.WorkspaceId == workspaceId && t.IssueId == issueId, ct))
            return WorkspaceAccess.Missing(context);
        if (!Workspaces.ReadPage(context, out var page, out var size)) return ApiProblem.Invalid(context);

        var entries = db.IssueActivity.AsNoTracking().Where(a => a.WorkspaceId == workspaceId && a.IssueId == issueId);
        var total = await entries.CountAsync(ct);
        var offset = (long)(page - 1) * size;
        var items = offset >= total ? [] : await entries.OrderByDescending(a => a.OccurredAt).ThenByDescending(a => a.Id)
            .Skip((int)offset).Take(size)
            .Select(a => new ActivityView(a.Id, a.IssueId, a.ActorId,
                db.Users.Where(u => u.Id == a.ActorId).Select(u => u.DisplayName).SingleOrDefault(),
                a.ActorId == null ? "system" : "user", a.Kind, a.OccurredAt, a.Changes, a.Reason))
            .ToArrayAsync(ct);
        return Results.Ok(new { items, page, pageSize = size, total });
    }

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
            var before = triage.Status;
            triage.Status = "needsReview";
            triage.AssigneeId = null;
            triage.EnteredReviewAt = now;
            Record(db, triage, actor, operationId, "assignmentReturned", now,
                [new("status", before, triage.Status), new("assigneeId", assignee, null)],
                reason: "The assignee left the workspace.");
        }
    }
}
