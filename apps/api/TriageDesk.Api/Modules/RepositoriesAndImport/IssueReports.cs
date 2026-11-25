using System.Data;
using Microsoft.EntityFrameworkCore;
using TriageDesk.Api.Http;
using TriageDesk.Api.Modules.IdentityAndWorkspaces;
using TriageDesk.Api.Persistence;

namespace TriageDesk.Api.Modules.RepositoriesAndImport;

internal static class IssueReports
{
    internal static void MapIssueReports(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/workspaces/{workspaceId:guid}/reports").RequireAuthorization();
        group.MapGet("/overview", Overview);
        group.MapGet("/workload", Workload);
    }

    private static async Task<IResult> Overview(Guid workspaceId, HttpContext context, WorkspaceAccess access,
        TriageDeskDbContext db, CancellationToken ct)
    {
        await using var read = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        if (await access.ReadAsync(workspaceId, WorkspaceAccess.Caller(context), ct) is null)
            return WorkspaceAccess.Missing(context);
        if (context.Request.Query.Count > 0) return ApiProblem.Invalid(context);

        var visible = Issues.Visible(db, workspaceId);
        var needsReview = Issues.Queue(visible, "needsReview");
        var count = await needsReview.CountAsync(ct);
        var oldest = count == 0 ? null : await Issues.Ordered(needsReview, "enteredReviewAt", descending: false)
            .ThenBy(row => row.Snapshot.Id)
            .Select(row => new { row.Snapshot.Id, row.Triage.EnteredReviewAt }).FirstAsync(ct);
        var available = await Issues.Queue(visible, "available").CountAsync(ct);
        await read.CommitAsync(ct);
        return Results.Ok(new { needsReview = count, availableToClaim = available,
            oldestIssueId = oldest?.Id, oldestEnteredReviewAt = oldest?.EnteredReviewAt });
    }

    private static async Task<IResult> Workload(Guid workspaceId, HttpContext context, WorkspaceAccess access,
        TriageDeskDbContext db, CancellationToken ct)
    {
        await using var read = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        if (await access.ReadAsync(workspaceId, WorkspaceAccess.Caller(context), ct) is null)
            return WorkspaceAccess.Missing(context);
        if (context.Request.Query.Count > 0) return ApiProblem.Invalid(context);

        var members = await (from member in db.WorkspaceMembers
                             join account in db.Users on member.UserId equals account.Id
                             where member.WorkspaceId == workspaceId && member.RemovedAt == null
                             orderby account.DisplayName, member.UserId
                             select new { member.UserId, account.DisplayName }).ToArrayAsync(ct);
        var visible = Issues.Visible(db, workspaceId);
        var counts = await Issues.Queue(visible, "activeWork").Where(row => row.Triage.AssigneeId != null)
            .GroupBy(row => row.Triage.AssigneeId!.Value)
            .Select(group => new { UserId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(group => group.UserId, group => group.Count, ct);
        var unassigned = await Issues.Queue(visible, "available").CountAsync(ct);
        await read.CommitAsync(ct);
        return Results.Ok(new { byAssignee = members.Select(member => new
        {
            member.UserId, member.DisplayName, activeIssues = counts.GetValueOrDefault(member.UserId)
        }), unassignedReady = unassigned });
    }
}
