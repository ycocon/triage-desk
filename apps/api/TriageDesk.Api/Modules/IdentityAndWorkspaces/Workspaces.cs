using System.Globalization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using TriageDesk.Api.Http;
using TriageDesk.Api.Persistence;

namespace TriageDesk.Api.Modules.IdentityAndWorkspaces;

internal sealed record CreateWorkspace(string? Name);
internal sealed record WorkspaceView
{
    public required Guid WorkspaceId { get; init; }
    public required string Name { get; init; }
    public required string MyRole { get; init; }
}

internal static class Workspaces
{
    internal static void MapWorkspaces(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/workspaces").RequireAuthorization();
        group.MapGet("", List);
        group.MapPost("", Create);
        group.MapGet("/{workspaceId:guid}", Get);
    }

    private static IQueryable<WorkspaceView> VisibleTo(TriageDeskDbContext db, Guid userId) =>
        from membership in db.WorkspaceMembers.AsNoTracking()
        join workspace in db.Workspaces.AsNoTracking() on membership.WorkspaceId equals workspace.Id
        where membership.UserId == userId && membership.RemovedAt == null
        select new WorkspaceView
        {
            WorkspaceId = workspace.Id,
            Name = workspace.Name,
            MyRole = workspace.OwnerUserId == userId ? "owner" : membership.BaseRole
        };

    private static Guid Caller(HttpContext context, UserManager<Account> users) =>
        Guid.Parse(users.GetUserId(context.User)!);

    private static async Task<IResult> List(HttpContext context, UserManager<Account> users,
        TriageDeskDbContext db, CancellationToken ct)
    {
        if (!ReadPage(context, out var page, out var size))
            return ApiProblem.Invalid(context);

        var visible = VisibleTo(db, Caller(context, users));
        var total = await visible.CountAsync(ct);
        var offset = (long)(page - 1) * size;
        var items = offset >= total ? [] : await visible.OrderBy(w => w.Name).ThenBy(w => w.WorkspaceId)
            .Skip((int)offset).Take(size).ToArrayAsync(ct);
        return Results.Ok(new { items, page, pageSize = size, total });
    }

    internal static bool ReadPage(HttpContext context, out int page, out int size, params string[] extraKeys)
    {
        var query = context.Request.Query;
        var pageIsValid = TryPage(query, "page", 1, int.MaxValue, out page);
        var sizeIsValid = TryPage(query, "pageSize", 25, 100, out size);
        return pageIsValid && sizeIsValid && query.Keys.All(key => key is "page" or "pageSize" ||
            (extraKeys.Contains(key) && query[key].Count == 1));
    }

    private static bool TryPage(IQueryCollection query, string name, int fallback, int max, out int value)
    {
        value = fallback;
        return !query.TryGetValue(name, out var raw) ||
            (raw.Count == 1 && int.TryParse(raw[0], NumberStyles.None, CultureInfo.InvariantCulture, out value)
             && value >= 1 && value <= max);
    }

    private static async Task<IResult> Create(CreateWorkspace request, HttpContext context,
        UserManager<Account> users, TriageDeskDbContext db, CancellationToken ct)
    {
        if (!Accounts.ValidName(request.Name)) return ApiProblem.Invalid(context);
        var ownerId = Caller(context, users);
        var now = DateTimeOffset.UtcNow;
        var workspace = new Workspace
        {
            Id = Guid.CreateVersion7(), Name = request.Name!.Trim(), OwnerUserId = ownerId,
            CreatedAt = now, UpdatedAt = now
        };
        db.Workspaces.Add(workspace);
        db.WorkspaceMembers.Add(new WorkspaceMember
        {
            WorkspaceId = workspace.Id, UserId = ownerId, JoinedAt = now, UpdatedAt = now
        });
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/v1/workspaces/{workspace.Id}",
            new WorkspaceView { WorkspaceId = workspace.Id, Name = workspace.Name, MyRole = "owner" });
    }

    private static async Task<IResult> Get(Guid workspaceId, HttpContext context, UserManager<Account> users,
        TriageDeskDbContext db, CancellationToken ct)
    {
        var workspace = await VisibleTo(db, Caller(context, users))
            .SingleOrDefaultAsync(w => w.WorkspaceId == workspaceId, ct);
        return workspace is null
            ? ApiProblem.Result(context, 404, "not_found", "This workspace could not be found.")
            : Results.Ok(workspace);
    }
}
