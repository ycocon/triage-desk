using Microsoft.EntityFrameworkCore;
using TriageDesk.Api.Http;
using TriageDesk.Api.Persistence;
using TriageDesk.Api.Modules.TriageAndActivity;

namespace TriageDesk.Api.Modules.IdentityAndWorkspaces;

internal sealed record ChangeRole(string? Role);
internal sealed record TransferOwner(Guid NewOwnerId);
internal sealed record MemberView(Guid UserId, string DisplayName, string Role);

internal static class TeamMembers
{
    internal static void MapTeamMembers(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/workspaces/{workspaceId:guid}").RequireAuthorization();
        group.MapGet("/members", List);
        group.MapPatch("/members/{userId:guid}", Change);
        group.MapDelete("/members/{userId:guid}", Remove);
        group.MapDelete("/membership", Leave);
        group.MapPut("/ownership", Transfer);
    }

    internal static bool ValidRole(string? role) => role is "member" or "reviewer";

    private static async Task<IResult> List(Guid workspaceId, HttpContext context, WorkspaceAccess access,
        TriageDeskDbContext db, CancellationToken ct)
    {
        if (await access.ReadAsync(workspaceId, WorkspaceAccess.Caller(context), ct) is not { } workspace)
            return WorkspaceAccess.Missing(context);
        if (!Workspaces.ReadPage(context, out var page, out var size)) return ApiProblem.Invalid(context);

        var members = from member in db.WorkspaceMembers.AsNoTracking()
                      join account in db.Users.AsNoTracking() on member.UserId equals account.Id
                      where member.WorkspaceId == workspaceId && member.RemovedAt == null
                      orderby account.DisplayName, member.UserId
                      select new MemberView(member.UserId, account.DisplayName,
                          member.UserId == workspace.OwnerUserId ? "owner" : member.BaseRole);
        var total = await members.CountAsync(ct);
        var offset = (long)(page - 1) * size;
        var items = offset >= total ? [] : await members.Skip((int)offset).Take(size).ToArrayAsync(ct);
        return Results.Ok(new { items, page, pageSize = size, total });
    }

    private static Task<WorkspaceMember?> FindActive(TriageDeskDbContext db, Guid workspaceId, Guid id,
        CancellationToken ct) => db.WorkspaceMembers.SingleOrDefaultAsync(
            m => m.WorkspaceId == workspaceId && m.UserId == id && m.RemovedAt == null, ct);

    private static Task<IResult> Change(Guid workspaceId, Guid userId, ChangeRole request, HttpContext context,
        WorkspaceAccess access, TriageDeskDbContext db, TimeProvider clock, CancellationToken ct) =>
        access.ChangeAsync(workspaceId, context, ownerOnly: true, async workspace =>
        {
            if (!ValidRole(request.Role)) return ApiProblem.Invalid(context);
            if (userId == workspace.OwnerUserId) return TransferFirst(context);
            var member = await FindActive(db, workspaceId, userId, ct);
            if (member is null) return WorkspaceAccess.Missing(context);
            var name = await db.Users.Where(user => user.Id == userId).Select(user => user.DisplayName).SingleAsync(ct);
            if (request.Role != member.BaseRole)
            {
                var now = clock.GetUtcNow();
                access.Audit(workspaceId, WorkspaceAccess.Caller(context), "role_changed", now,
                    userId, previousRole: member.BaseRole, nextRole: request.Role);
                member.BaseRole = request.Role!;
                member.UpdatedAt = now;
            }
            return Results.Ok(new MemberView(userId, name, member.BaseRole));
        }, ct);

    private static Task<IResult> Remove(Guid workspaceId, Guid userId, HttpContext context,
        WorkspaceAccess access, TriageDeskDbContext db, TimeProvider clock, CancellationToken ct) =>
        Retire(workspaceId, userId, context, access, db, clock, self: false, ct);

    private static Task<IResult> Leave(Guid workspaceId, HttpContext context,
        WorkspaceAccess access, TriageDeskDbContext db, TimeProvider clock, CancellationToken ct) =>
        Retire(workspaceId, WorkspaceAccess.Caller(context), context, access, db, clock, self: true, ct);

    private static Task<IResult> Retire(Guid workspaceId, Guid userId, HttpContext context,
        WorkspaceAccess access, TriageDeskDbContext db, TimeProvider clock, bool self, CancellationToken ct) =>
        access.ChangeAsync(workspaceId, context, ownerOnly: !self, async workspace =>
        {
            if (userId == workspace.OwnerUserId) return TransferFirst(context);
            var member = await FindActive(db, workspaceId, userId, ct);
            if (member is null) return WorkspaceAccess.Missing(context);
            var now = clock.GetUtcNow();
            member.RemovedAt = member.UpdatedAt = now;
            await IssueHistory.ReturnRetiredAssignments(db, workspaceId, userId, WorkspaceAccess.Caller(context), now, ct);
            access.Audit(workspaceId, WorkspaceAccess.Caller(context),
                self ? "member_left" : "member_removed", now, userId, previousRole: member.BaseRole);
            return Results.NoContent();
        }, ct);

    private static Task<IResult> Transfer(Guid workspaceId, TransferOwner request, HttpContext context,
        WorkspaceAccess access, TriageDeskDbContext db, TimeProvider clock, CancellationToken ct) =>
        access.ChangeAsync(workspaceId, context, ownerOnly: true, async workspace =>
        {
            var caller = WorkspaceAccess.Caller(context);
            if (request.NewOwnerId == Guid.Empty) return ApiProblem.Invalid(context);
            if (request.NewOwnerId == caller)
                return WorkspaceAccess.Conflict(context, "already_owner", "This teammate is already the Owner.");
            var recipient = await FindActive(db, workspaceId, request.NewOwnerId, ct);
            if (recipient is null || !await db.Users.AnyAsync(u => u.Id == recipient.UserId && !u.Disabled, ct))
                return WorkspaceAccess.Missing(context);
            var previous = (await FindActive(db, workspaceId, caller, ct))!;
            var now = clock.GetUtcNow();
            workspace.OwnerUserId = recipient.UserId;
            workspace.UpdatedAt = now;
            access.Audit(workspaceId, caller, "ownership_transferred", now, recipient.UserId,
                previousRole: recipient.BaseRole, nextRole: "owner");
            return Results.Ok(new WorkspaceView { WorkspaceId = workspaceId, Name = workspace.Name, MyRole = previous.BaseRole });
        }, ct);

    private static IResult TransferFirst(HttpContext context) =>
        WorkspaceAccess.Conflict(context, "owner_transfer_required", "Transfer ownership before removing the Owner.");
}
