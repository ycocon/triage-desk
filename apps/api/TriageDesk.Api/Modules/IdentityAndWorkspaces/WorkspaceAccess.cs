using System.Diagnostics;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using TriageDesk.Api.Http;
using TriageDesk.Api.Operations;
using TriageDesk.Api.Persistence;

namespace TriageDesk.Api.Modules.IdentityAndWorkspaces;

internal sealed class WorkspaceAccess(TriageDeskDbContext db, ILogger<WorkspaceAccess> logger, ReleaseOperations release)
{
    internal static Guid Caller(HttpContext context) =>
        Guid.Parse(context.User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    internal async Task<Workspace?> ReadAsync(Guid workspaceId, Guid caller, CancellationToken ct) =>
        await db.Workspaces.AsNoTracking().SingleOrDefaultAsync(w => w.Id == workspaceId &&
            db.WorkspaceMembers.Any(m => m.WorkspaceId == w.Id && m.UserId == caller && m.RemovedAt == null), ct);

    internal async Task<IResult> ChangeAsync(Guid workspaceId, HttpContext context, bool ownerOnly,
        Func<Workspace, Task<IResult>> change, CancellationToken ct) =>
        await LockedAsync(workspaceId, context, async workspace =>
        {
            var caller = Caller(context);
            if (!await db.WorkspaceMembers.AnyAsync(m => m.WorkspaceId == workspace.Id &&
                m.UserId == caller && m.RemovedAt == null, ct))
                return Missing(context);
            if (ownerOnly && workspace.OwnerUserId != caller) return Forbidden(context);
            return await change(workspace);
        }, ct);

    // All writes that affect workspace access lock the workspace before inspecting memberships.
    internal async Task<IResult> LockedAsync(Guid workspaceId, HttpContext context,
        Func<Workspace, Task<IResult>> change, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var rows = await db.Workspaces
            .FromSql($"""SELECT * FROM "Workspaces" WHERE "Id" = {workspaceId} FOR UPDATE""")
            .ToListAsync(ct);
        if (rows.SingleOrDefault() is not { } workspace) return Missing(context);

        var result = await change(workspace);
        if (result is IStatusCodeHttpResult { StatusCode: >= 400 }) return result;
        var audit = db.ChangeTracker.Entries<TeamAuditEntry>().Where(entry => entry.State == EntityState.Added)
            .Select(entry => entry.Entity).ToArray();
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        foreach (var entry in audit)
            logger.LogInformation("Committed admin event {EventId} {ActionName} {OccurredAt} {WorkspaceId} {ActorId} " +
                "{SubjectId} {InvitationId} {PreviousRole} {NextRole} {TraceId} {Release}",
                entry.Id, entry.Action, entry.OccurredAt, entry.WorkspaceId, entry.ActorUserId, entry.SubjectUserId,
                entry.InvitationId, entry.PreviousRole, entry.NextRole,
                Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier, release.Release);
        return result;
    }

    internal static IResult Missing(HttpContext context) =>
        ApiProblem.Result(context, 404, "not_found", "This resource could not be found.");

    internal static IResult Forbidden(HttpContext context) =>
        ApiProblem.Result(context, 403, "forbidden", "Only the workspace Owner can do that.");

    internal static IResult Conflict(HttpContext context, string code, string title) =>
        ApiProblem.Result(context, 409, code, title);

    internal void Audit(Guid workspaceId, Guid actor, string action, DateTimeOffset time,
        Guid? subjectUser = null, Guid? invitation = null, string? previousRole = null, string? nextRole = null) =>
        db.TeamAudit.Add(new TeamAuditEntry
        {
            Id = Guid.CreateVersion7(), WorkspaceId = workspaceId, ActorUserId = actor,
            Action = action, OccurredAt = time, SubjectUserId = subjectUser, InvitationId = invitation,
            PreviousRole = previousRole, NextRole = nextRole
        });
}
