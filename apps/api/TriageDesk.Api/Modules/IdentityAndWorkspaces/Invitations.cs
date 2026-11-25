using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using TriageDesk.Api.Http;
using TriageDesk.Api.Persistence;

namespace TriageDesk.Api.Modules.IdentityAndWorkspaces;

internal sealed record IssueInvitation(string? Email, string? Role);
internal sealed record InvitationProof(string? Token);
internal sealed record InvitationView(Guid InvitationId, string Email, string Role, string State, DateTimeOffset ExpiresAt);

internal static class Invitations
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);
    private static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    internal static void MapInvitations(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/workspaces/{workspaceId:guid}/invitations").RequireAuthorization();
        group.MapGet("", List);
        group.MapPost("", Create);
        group.MapDelete("/{invitationId:guid}", Revoke);

        var recipient = api.MapGroup("/invitations").RequireAuthorization();
        recipient.MapPost("/preview", Preview);
        recipient.MapPost("/accept", Accept);
    }

    private static async Task<IResult> List(Guid workspaceId, HttpContext context, WorkspaceAccess access,
        TriageDeskDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var caller = WorkspaceAccess.Caller(context);
        if (await access.ReadAsync(workspaceId, caller, ct) is not { } workspace)
            return WorkspaceAccess.Missing(context);
        if (workspace.OwnerUserId != caller) return WorkspaceAccess.Forbidden(context);
        if (!Workspaces.ReadPage(context, out var page, out var size)) return ApiProblem.Invalid(context);

        var now = clock.GetUtcNow();
        var cutoff = now - Retention;
        var visible = db.Invitations.AsNoTracking().Where(i => i.WorkspaceId == workspaceId &&
            (i.ResolvedAt ?? i.ExpiresAt) > cutoff);
        var total = await visible.CountAsync(ct);
        var offset = (long)(page - 1) * size;
        var items = offset >= total ? [] : await visible.OrderByDescending(i => i.IssuedAt).ThenBy(i => i.Id)
            .Skip((int)offset).Take(size)
            .Select(i => new InvitationView(i.Id, i.Email, i.Role,
                i.State == "pending" && i.ExpiresAt <= now ? "expired" : i.State, i.ExpiresAt))
            .ToArrayAsync(ct);
        return Results.Ok(new { items, page, pageSize = size, total });
    }

    private static Task<IResult> Create(Guid workspaceId, IssueInvitation request, HttpContext context,
        WorkspaceAccess access, TriageDeskDbContext db, UserManager<Account> accounts, IConfiguration config,
        AccountThrottle limits, TimeProvider clock, CancellationToken ct) =>
        access.ChangeAsync(workspaceId, context, ownerOnly: true, async _ =>
        {
            var email = Accounts.ValidEmail(request.Email);
            if (email is null || !TeamMembers.ValidRole(request.Role)) return ApiProblem.Invalid(context);
            var caller = WorkspaceAccess.Caller(context);
            if (!limits.AllowInvitation(caller, "issue")) return TooMany(context);

            var normalizedEmail = accounts.NormalizeEmail(email)!;
            var now = clock.GetUtcNow();
            var cutoff = now - Retention;
            if (await db.WorkspaceMembers.AnyAsync(m => m.WorkspaceId == workspaceId && m.RemovedAt == null &&
                db.Users.Any(u => u.Id == m.UserId && u.NormalizedEmail == normalizedEmail), ct))
                return AlreadyMember(context);

            await db.Invitations.Where(i => i.WorkspaceId == workspaceId && (i.ResolvedAt ?? i.ExpiresAt) <= cutoff)
                .ExecuteDeleteAsync(ct);

            var pending = await db.Invitations.AsNoTracking().SingleOrDefaultAsync(i =>
                i.WorkspaceId == workspaceId && i.NormalizedEmail == normalizedEmail && i.State == "pending", ct);
            if (pending is not null)
            {
                if (pending.ExpiresAt > now)
                    return WorkspaceAccess.Conflict(context, "invitation_pending", "A pending invitation already exists for this email.");
                // Free the partial unique index entry before inserting its replacement.
                await db.Invitations.Where(i => i.WorkspaceId == workspaceId && i.Id == pending.Id)
                    .ExecuteUpdateAsync(update => update.SetProperty(i => i.State, "expired")
                        .SetProperty(i => i.ResolvedAt, i => (DateTimeOffset?)i.ExpiresAt), ct);
            }

            var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            var invitation = new WorkspaceInvitation
            {
                WorkspaceId = workspaceId, Id = Guid.CreateVersion7(), Email = email, NormalizedEmail = normalizedEmail,
                Role = request.Role!, TokenDigest = Digest(token), InviterUserId = caller,
                IssuedAt = now, ExpiresAt = now + Lifetime
            };
            db.Invitations.Add(invitation);
            access.Audit(workspaceId, caller, "invitation_created", now, invitation: invitation.Id, nextRole: invitation.Role);

            var origin = new Uri(config["Identity:WebOrigin"] ?? "https://localhost:3000");
            var acceptUrl = new Uri(origin, $"/join#token={token}");
            return Results.Json(new { invitation = View(invitation), acceptUrl }, statusCode: 201);
        }, ct);

    private static Task<IResult> Revoke(Guid workspaceId, Guid invitationId, HttpContext context,
        WorkspaceAccess access, TriageDeskDbContext db, TimeProvider clock, CancellationToken ct) =>
        access.ChangeAsync(workspaceId, context, ownerOnly: true, async _ =>
        {
            var invitation = await db.Invitations.FindAsync([workspaceId, invitationId], ct);
            if (invitation is null) return WorkspaceAccess.Missing(context);
            if (invitation.State == "revoked") return Results.NoContent();

            var now = clock.GetUtcNow();
            if (EffectiveState(invitation, now) != "pending")
                return WorkspaceAccess.Conflict(context, "invitation_not_pending", "Only pending invitations can be revoked.");
            invitation.State = "revoked";
            invitation.ResolvedAt = now;
            access.Audit(workspaceId, WorkspaceAccess.Caller(context), "invitation_revoked", now, invitation: invitationId);
            return Results.NoContent();
        }, ct);

    private static async Task<IResult> Preview(InvitationProof proof, HttpContext context,
        TriageDeskDbContext db, AccountThrottle limits, TimeProvider clock, CancellationToken ct)
    {
        var caller = WorkspaceAccess.Caller(context);
        if (!limits.AllowInvitation(caller, "preview")) return TooMany(context);
        var now = clock.GetUtcNow();
        var invitation = await ForRecipient(proof.Token, caller, now, db, ct);
        if (invitation is null) return WorkspaceAccess.Missing(context);

        var state = EffectiveState(invitation, now);
        if (state is "expired" or "revoked") return Unavailable(context, state);
        if (state == "accepted" && (invitation.AcceptedByUserId != caller ||
            !await db.WorkspaceMembers.AnyAsync(m => m.WorkspaceId == invitation.WorkspaceId &&
                m.UserId == caller && m.RemovedAt == null, ct)))
            return WorkspaceAccess.Missing(context);

        var workspaceName = await db.Workspaces.Where(w => w.Id == invitation.WorkspaceId).Select(w => w.Name).SingleAsync(ct);
        return Results.Ok(new { workspaceName, role = invitation.Role, expiresAt = invitation.ExpiresAt });
    }

    private static async Task<IResult> Accept(InvitationProof proof, HttpContext context, WorkspaceAccess access,
        TriageDeskDbContext db, AccountThrottle limits, TimeProvider clock, CancellationToken ct)
    {
        var caller = WorkspaceAccess.Caller(context);
        if (!limits.AllowInvitation(caller, "accept")) return TooMany(context);
        var candidate = await ForRecipient(proof.Token, caller, clock.GetUtcNow(), db, ct);
        if (candidate is null) return WorkspaceAccess.Missing(context);

        return await access.LockedAsync(candidate.WorkspaceId, context, async workspace =>
        {
            // Recheck proof, recipient and invitation state after waiting for the workspace lock.
            var now = clock.GetUtcNow();
            if (await ForRecipient(proof.Token, caller, now, db, ct) is not { } current)
                return WorkspaceAccess.Missing(context);
            var invitation = (await db.Invitations.FindAsync([workspace.Id, current.Id], ct))!;
            var member = await db.WorkspaceMembers.FindAsync([workspace.Id, caller], ct);
            var state = EffectiveState(invitation, now);
            if (state is "expired" or "revoked") return Unavailable(context, state);
            if (state == "accepted")
                return invitation.AcceptedByUserId == caller && member is { RemovedAt: null }
                    ? Results.Ok(WorkspaceFor(workspace, member))
                    : WorkspaceAccess.Conflict(context, "invitation_used", "Ask the Owner for a new invitation.");
            if (member is { RemovedAt: null }) return AlreadyMember(context);

            if (member is null)
            {
                member = new WorkspaceMember { WorkspaceId = workspace.Id, UserId = caller };
                db.WorkspaceMembers.Add(member);
            }
            member.BaseRole = invitation.Role;
            member.JoinedAt = member.UpdatedAt = now;
            member.RemovedAt = null;
            invitation.State = "accepted";
            invitation.AcceptedByUserId = caller;
            invitation.ResolvedAt = now;
            access.Audit(workspace.Id, caller, "invitation_accepted", now,
                caller, invitation.Id, nextRole: member.BaseRole);
            return Results.Ok(WorkspaceFor(workspace, member));
        }, ct);
    }

    private static async Task<WorkspaceInvitation?> ForRecipient(string? token, Guid userId, DateTimeOffset now,
        TriageDeskDbContext db, CancellationToken ct)
    {
        if (token is not { Length: 43 } ||
            token.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
            return null;

        var digest = Digest(token);
        var cutoff = now - Retention;
        return await db.Invitations.AsNoTracking().SingleOrDefaultAsync(i => i.TokenDigest == digest &&
            (i.ResolvedAt ?? i.ExpiresAt) > cutoff &&
            db.Users.Any(u => u.Id == userId && u.EmailConfirmed && !u.Disabled &&
                u.NormalizedEmail == i.NormalizedEmail), ct);
    }

    private static string Digest(string token) => Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(token)));

    private static string EffectiveState(WorkspaceInvitation invitation, DateTimeOffset now) =>
        invitation.State == "pending" && invitation.ExpiresAt <= now ? "expired" : invitation.State;

    private static InvitationView View(WorkspaceInvitation i) => new(i.Id, i.Email, i.Role, i.State, i.ExpiresAt);

    private static WorkspaceView WorkspaceFor(Workspace workspace, WorkspaceMember member) =>
        new() { WorkspaceId = workspace.Id, Name = workspace.Name,
            MyRole = workspace.OwnerUserId == member.UserId ? "owner" : member.BaseRole };

    private static IResult AlreadyMember(HttpContext context) =>
        WorkspaceAccess.Conflict(context, "already_member", "This account is already a member of the workspace.");

    private static IResult Unavailable(HttpContext context, string state) =>
        ApiProblem.Result(context, 410, "invitation_" + state, "This invitation is no longer available.");

    private static IResult TooMany(HttpContext context)
    {
        context.Response.Headers.RetryAfter = "900";
        return ApiProblem.Result(context, 429, "rate_limited", "Too many invitation requests. Try again later.");
    }
}
