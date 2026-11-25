using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using TriageDesk.Api.Http;
using TriageDesk.Api.Modules.IdentityAndWorkspaces;
using TriageDesk.Api.Persistence;

namespace TriageDesk.Api.Modules.TriageAndActivity;

internal sealed record ClaimIssue(Guid CommandId, int ExpectedVersion);

internal static class IssueDecisions
{
    private sealed record AcceptIssue(string Kind, Guid CommandId, int ExpectedVersion, string? Priority,
        string? NextStep, Guid? AssigneeId, string? Severity);
    private sealed record ProgressIssue(string Kind, Guid CommandId, int ExpectedVersion);
    private sealed record Decision(string Kind, Guid Id, int Version, string? Priority = null,
        string? NextStep = null, Guid? Assignee = null);

    private static readonly JsonSerializerOptions StrictJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    internal static void MapIssueDecisions(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/workspaces/{workspaceId:guid}/issues/{issueId:guid}").RequireAuthorization();
        group.MapPost("/claim", Claim);
        group.MapPost("/commands", Apply);
        group.MapGet("/activity", IssueHistory.List);
    }

    private static Task<IResult> Claim(Guid workspaceId, Guid issueId, ClaimIssue request, HttpContext context,
        WorkspaceAccess access, TriageDeskDbContext db, TimeProvider clock, CancellationToken ct) =>
        Execute(workspaceId, issueId, new Decision("claim", request.CommandId, request.ExpectedVersion),
            context, access, db, clock, ct);

    private static Task<IResult> Apply(Guid workspaceId, Guid issueId, JsonElement body, HttpContext context,
        WorkspaceAccess access, TriageDeskDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var decision = Parse(body);
        return decision is null ? Task.FromResult(ApiProblem.Invalid(context)) :
            Execute(workspaceId, issueId, decision, context, access, db, clock, ct);
    }

    private static Decision? Parse(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("kind", out var kind) ||
            kind.ValueKind != JsonValueKind.String) return null;
        try
        {
            switch (kind.GetString())
            {
                case "accept":
                    var accept = body.Deserialize<AcceptIssue>(StrictJson)!;
                    if (accept.Priority is not ("high" or "normal" or "low") ||
                        accept.NextStep is not { Length: >= 1 and <= 2000 } || string.IsNullOrWhiteSpace(accept.NextStep)
                        || accept.Severity is not null || accept.AssigneeId == Guid.Empty)
                        return null;
                    return new Decision(accept.Kind, accept.CommandId, accept.ExpectedVersion,
                        accept.Priority, accept.NextStep.Trim(), accept.AssigneeId);
                case "start":
                case "complete":
                    var progress = body.Deserialize<ProgressIssue>(StrictJson)!;
                    return new Decision(progress.Kind, progress.CommandId, progress.ExpectedVersion);
                default:
                    return null;
            }
        }
        catch (JsonException) { return null; }
    }

    private static Task<IResult> Execute(Guid workspaceId, Guid issueId, Decision decision, HttpContext context,
        WorkspaceAccess access, TriageDeskDbContext db, TimeProvider clock, CancellationToken ct) =>
        access.ChangeAsync(workspaceId, context, ownerOnly: false, async workspace =>
        {
            var triage = await db.IssueTriage.FindAsync([workspaceId, issueId], ct);
            if (triage is null) return WorkspaceAccess.Missing(context);
            var visible = await db.IssueSnapshots.AnyAsync(i => i.WorkspaceId == workspaceId && i.Id == issueId &&
                i.SourceAvailable && db.Repositories.Any(r => r.WorkspaceId == workspaceId &&
                    r.Id == i.RepositoryId && r.Public && r.DisconnectedAt == null), ct);
            if (!visible) return Conflict(context, triage, "source_unavailable", "The issue source is unavailable.");
            if (decision.Id == Guid.Empty || decision.Version < 1) return ApiProblem.Invalid(context);

            var actor = WorkspaceAccess.Caller(context);
            if (decision.Kind == "accept" && workspace.OwnerUserId != actor &&
                !await db.WorkspaceMembers.AnyAsync(m => m.WorkspaceId == workspaceId && m.UserId == actor &&
                    m.RemovedAt == null && m.BaseRole == "reviewer", ct))
                return ApiProblem.Result(context, 403, "forbidden", "Only an Owner or Reviewer can accept this issue.");
            if (decision.Kind is "start" or "complete" && triage.AssigneeId != actor)
                return ApiProblem.Result(context, 403, "forbidden", "Only the current assignee can update this issue.");

            var digest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(decision)));
            var prior = await db.IssueActivity.AsNoTracking().SingleOrDefaultAsync(a =>
                a.WorkspaceId == workspaceId && a.IssueId == issueId && a.OperationId == decision.Id && a.Ordinal == 0, ct);
            if (prior is not null)
                return prior.ActorId == actor && prior.CommandDigest == digest ? Results.Ok(View(triage)) :
                    Conflict(context, triage, "command_id_reused", "This command identifier was already used.");
            if (decision.Kind == "claim" && triage.Status == "ready" && triage.AssigneeId is not null)
                return Conflict(context, triage, "already_claimed", "This issue has already been claimed.");
            if (decision.Version != triage.DecisionVersion)
                return Conflict(context, triage, "stale_decision", "The issue changed while you were viewing it.");

            var validStatus = decision.Kind switch { "accept" => "needsReview", "complete" => "inProgress", _ => "ready" };
            if (triage.Status != validStatus)
                return Conflict(context, triage, "invalid_transition", "The current status does not allow this action.");
            if (decision.Assignee is { } assignee && !await db.WorkspaceMembers.AnyAsync(m =>
                m.WorkspaceId == workspaceId && m.UserId == assignee && m.RemovedAt == null &&
                db.Users.Any(u => u.Id == assignee && !u.Disabled), ct))
                return WorkspaceAccess.Missing(context);

            var changes = new List<ActivityChange>(4);
            string kind;
            switch (decision.Kind)
            {
                case "accept":
                    changes.Add(new("priority", triage.Priority, decision.Priority));
                    changes.Add(new("nextStep", triage.NextStep, decision.NextStep));
                    if (decision.Assignee is not null) changes.Add(new("assigneeId", null, decision.Assignee));
                    triage.Priority = decision.Priority;
                    triage.NextStep = decision.NextStep;
                    triage.AssigneeId = decision.Assignee;
                    triage.EnteredReviewAt = null;
                    SetStatus(triage, changes, "ready");
                    kind = "accepted";
                    break;
                case "claim":
                    changes.Add(new("assigneeId", null, actor));
                    triage.AssigneeId = actor;
                    kind = "claimed";
                    break;
                case "start":
                    SetStatus(triage, changes, "inProgress");
                    kind = "started";
                    break;
                default:
                    SetStatus(triage, changes, "done");
                    kind = "completed";
                    break;
            }

            IssueHistory.Record(db, triage, actor, decision.Id, kind, clock.GetUtcNow(), changes, digest);
            return Results.Ok(View(triage));
        }, ct);

    private static void SetStatus(IssueTriage triage, List<ActivityChange> changes, string status)
    {
        changes.Add(new("status", triage.Status, status));
        triage.Status = status;
    }

    private static IResult Conflict(HttpContext context, IssueTriage triage, string code, string title) =>
        ApiProblem.Result(context, 409, code, title,
            new Dictionary<string, object?> { ["currentTriage"] = View(triage) });

    internal static object View(IssueTriage triage) => new
    {
        triage.Status, triage.Priority, triage.Severity, triage.AssigneeId, triage.NextStep, triage.Question,
        triage.DeferralReason, triage.SourceReviewFlag, triage.EnteredReviewAt, triage.UpdatedAt, triage.DecisionVersion
    };
}
