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
    private sealed record Command(string Kind, Guid CommandId, int ExpectedVersion);
    private sealed record AcceptIssue(string Kind, Guid CommandId, int ExpectedVersion, string? Priority,
        string? NextStep, Guid? AssigneeId, string? Severity);
    private sealed record RequestInformation(string Kind, Guid CommandId, int ExpectedVersion, Guid AssigneeId, string? Question);
    private sealed record Assign(string Kind, Guid CommandId, int ExpectedVersion, Guid? AssigneeId);
    private sealed record PriorityChange(string Kind, Guid CommandId, int ExpectedVersion, string? Priority);
    private sealed record SeverityChange(string Kind, Guid CommandId, int ExpectedVersion, string? Severity);
    private sealed record NextStepEdit(string Kind, Guid CommandId, int ExpectedVersion, string? NextStep);
    private sealed record ReasonedCommand(string Kind, Guid CommandId, int ExpectedVersion, string? Reason);
    private sealed record Answer(string Kind, Guid CommandId, int ExpectedVersion,
        [property: JsonPropertyName("answer")] string? AnswerText);

    // Preserve the original receipt format for commands saved before new payload fields were added.
    private sealed record Decision(string Kind, Guid Id, int Version, string? Priority = null,
        string? NextStep = null, Guid? Assignee = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Severity = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Note = null);

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
                    if (!ValidPriority(accept.Priority) || !ValidNote(accept.NextStep) ||
                        !ValidSeverity(accept.Severity) || accept.AssigneeId == Guid.Empty)
                        return null;
                    return new Decision(accept.Kind, accept.CommandId, accept.ExpectedVersion, accept.Priority,
                        accept.NextStep!.Trim(), accept.AssigneeId, accept.Severity);
                case "requestInformation":
                    var question = body.Deserialize<RequestInformation>(StrictJson)!;
                    if (question.AssigneeId == Guid.Empty || !ValidNote(question.Question)) return null;
                    return new Decision(question.Kind, question.CommandId, question.ExpectedVersion,
                        Assignee: question.AssigneeId, Note: question.Question!.Trim());
                case "reassign":
                    var assignment = body.Deserialize<Assign>(StrictJson)!;
                    if (!body.TryGetProperty("assigneeId", out _) || assignment.AssigneeId == Guid.Empty) return null;
                    return new Decision(assignment.Kind, assignment.CommandId, assignment.ExpectedVersion, Assignee: assignment.AssigneeId);
                case "changePriority":
                    var priority = body.Deserialize<PriorityChange>(StrictJson)!;
                    if (!ValidPriority(priority.Priority)) return null;
                    return new Decision(priority.Kind, priority.CommandId, priority.ExpectedVersion, Priority: priority.Priority);
                case "changeSeverity":
                    var severity = body.Deserialize<SeverityChange>(StrictJson)!;
                    if (!body.TryGetProperty("severity", out _) || !ValidSeverity(severity.Severity)) return null;
                    return new Decision(severity.Kind, severity.CommandId, severity.ExpectedVersion, Severity: severity.Severity);
                case "editNextStep":
                    var step = body.Deserialize<NextStepEdit>(StrictJson)!;
                    if (!ValidNote(step.NextStep)) return null;
                    return new Decision(step.Kind, step.CommandId, step.ExpectedVersion, NextStep: step.NextStep!.Trim());
                case "answer":
                    var answer = body.Deserialize<Answer>(StrictJson)!;
                    if (!ValidNote(answer.AnswerText)) return null;
                    return new Decision(answer.Kind, answer.CommandId, answer.ExpectedVersion, Note: answer.AnswerText!.Trim());
                case "setAside":
                case "returnToReview":
                case "acknowledgeSourceChange":
                    var reasoned = body.Deserialize<ReasonedCommand>(StrictJson)!;
                    if ((reasoned.Kind == "setAside" || body.TryGetProperty("reason", out _)) && !ValidNote(reasoned.Reason))
                        return null;
                    return new Decision(reasoned.Kind, reasoned.CommandId, reasoned.ExpectedVersion, Note: reasoned.Reason?.Trim());
                case "start":
                case "complete":
                    var progress = body.Deserialize<Command>(StrictJson)!;
                    return new Decision(progress.Kind, progress.CommandId, progress.ExpectedVersion);
                default:
                    return null;
            }
        }
        catch (JsonException) { return null; }
    }

    private static bool ValidPriority(string? value) => value is "high" or "normal" or "low";
    private static bool ValidSeverity(string? value) => value is null or "critical" or "major" or "minor" or "unknown";
    private static bool ValidNote(string? value) => value is { Length: <= 2000 } && !string.IsNullOrWhiteSpace(value);

    private static Task<IResult> Execute(Guid workspaceId, Guid issueId, Decision decision, HttpContext context,
        WorkspaceAccess access, TriageDeskDbContext db, TimeProvider clock, CancellationToken ct) =>
        access.ChangeAsync(workspaceId, context, ownerOnly: false, async workspace =>
        {
            var triage = await db.IssueTriage.FindAsync([workspaceId, issueId], ct);
            if (triage is null) return WorkspaceAccess.Missing(context);
            var labels = await db.IssueSnapshots.Where(i => i.WorkspaceId == workspaceId && i.Id == issueId &&
                i.SourceAvailable && db.Repositories.Any(r => r.WorkspaceId == workspaceId &&
                    r.Id == i.RepositoryId && r.Public && r.DisconnectedAt == null)).Select(i => i.Labels).SingleOrDefaultAsync(ct);
            if (labels is null) return Conflict(context, triage, "source_unavailable", "The issue source is unavailable.");
            if (decision.Id == Guid.Empty || decision.Version < 1) return ApiProblem.Invalid(context);

            var actor = WorkspaceAccess.Caller(context);
            var assigned = triage.AssigneeId == actor;
            var usesReviewerRole = decision.Kind is not ("claim" or "start" or "complete" or "answer") &&
                !(decision.Kind == "editNextStep" && assigned);
            if (usesReviewerRole && workspace.OwnerUserId != actor &&
                !await db.WorkspaceMembers.AnyAsync(m => m.WorkspaceId == workspaceId && m.UserId == actor &&
                    m.RemovedAt == null && m.BaseRole == "reviewer", ct))
                return ApiProblem.Result(context, 403, "forbidden", "Only an Owner or Reviewer can make this decision.");

            var digest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(decision)));
            var prior = await db.IssueActivity.AsNoTracking().SingleOrDefaultAsync(a =>
                a.WorkspaceId == workspaceId && a.IssueId == issueId && a.OperationId == decision.Id && a.Ordinal == 0, ct);
            var matchingReplay = prior?.ActorId == actor && prior.CommandDigest == digest;
            // An answer clears the assignment. Its author may replay the one saved answer.
            if (decision.Kind is "start" or "complete" or "answer" && !assigned &&
                !(decision.Kind == "answer" && matchingReplay))
                return ApiProblem.Result(context, 403, "forbidden", "Only the current assignee can update this issue.");

            if (prior is not null)
                return matchingReplay ? Results.Ok(View(triage)) :
                    Conflict(context, triage, "command_id_reused", "This command identifier was already used.");
            if (decision.Kind == "claim" && triage.Status == "ready" && triage.AssigneeId is not null)
                return Conflict(context, triage, "already_claimed", "This issue has already been claimed.");
            if (decision.Version != triage.DecisionVersion)
                return Conflict(context, triage, "stale_decision", "The issue changed while you were viewing it.");
            if (!AllowedState(decision, triage))
                return Conflict(context, triage, "invalid_transition", "The current status does not allow this action.");
            if (decision.Severity is not null && !labels.Contains("bug", StringComparer.OrdinalIgnoreCase))
                return Conflict(context, triage, "severity_requires_bug", "A GitHub bug label is required to set severity.");
            if (decision.Assignee is { } assignee && !await db.WorkspaceMembers.AnyAsync(m =>
                m.WorkspaceId == workspaceId && m.UserId == assignee && m.RemovedAt == null &&
                db.Users.Any(u => u.Id == assignee && !u.Disabled), ct))
                return WorkspaceAccess.Missing(context);

            var now = clock.GetUtcNow();
            var changes = new List<ActivityChange>(5);
            var (kind, reason) = Change(decision, triage, actor, now, changes);
            IssueHistory.Record(db, triage, actor, decision.Id, kind, now, changes, digest, reason);
            return Results.Ok(View(triage));
        }, ct);

    private static bool AllowedState(Decision decision, IssueTriage triage) => decision.Kind switch
    {
        "accept" or "requestInformation" or "setAside" => triage.Status == "needsReview",
        "claim" or "start" => triage.Status == "ready",
        "complete" => triage.Status == "inProgress",
        "answer" => triage.Status == "waitingForInformation" && triage.Question is not null,
        "returnToReview" => triage.Status is "done" or "setAside",
        "changePriority" or "editNextStep" => triage.Status is "ready" or "inProgress",
        "changeSeverity" => true,
        "reassign" => triage.Status == "ready" ||
            (decision.Assignee is not null && triage.Status is "inProgress" or "waitingForInformation"),
        "acknowledgeSourceChange" => triage.SourceReviewFlag is "closedWhileActive" or "reopenedAfterDecision",
        _ => false
    };

    private static (string Kind, string? Reason) Change(Decision decision, IssueTriage triage, Guid actor,
        DateTimeOffset now, List<ActivityChange> changes)
    {
        switch (decision.Kind)
        {
            case "accept":
                triage.Priority = Track(changes, "priority", triage.Priority, decision.Priority);
                triage.NextStep = Track(changes, "nextStep", triage.NextStep, decision.NextStep);
                triage.AssigneeId = Track(changes, "assigneeId", triage.AssigneeId, decision.Assignee);
                triage.Severity = Track(changes, "severity", triage.Severity, decision.Severity);
                Move("ready");
                return ("accepted", null);
            case "claim":
                triage.AssigneeId = Track<Guid?>(changes, "assigneeId", triage.AssigneeId, actor);
                return ("claimed", null);
            case "start":
                Move("inProgress");
                return ("started", null);
            case "complete":
                Move("done");
                return ("completed", null);
            case "requestInformation":
                triage.Question = Track(changes, "question", triage.Question, decision.Note);
                triage.AssigneeId = Track(changes, "assigneeId", triage.AssigneeId, decision.Assignee);
                Move("waitingForInformation");
                return ("informationRequested", null);
            case "answer":
                changes.Add(new("answer", null, decision.Note));
                ReturnToReview();
                return ("questionAnswered", null);
            case "setAside":
                triage.DeferralReason = Track(changes, "deferralReason", triage.DeferralReason, decision.Note);
                Move("setAside");
                return ("setAside", decision.Note);
            case "returnToReview":
                ReturnToReview();
                return ("returnedToReview", decision.Note);
            case "reassign":
                triage.AssigneeId = Track(changes, "assigneeId", triage.AssigneeId, decision.Assignee);
                return ("reassigned", null);
            case "changePriority":
                triage.Priority = Track(changes, "priority", triage.Priority, decision.Priority);
                return ("priorityChanged", null);
            case "changeSeverity":
                triage.Severity = Track(changes, "severity", triage.Severity, decision.Severity);
                return ("severityChanged", null);
            case "editNextStep":
                triage.NextStep = Track(changes, "nextStep", triage.NextStep, decision.NextStep);
                return ("nextStepEdited", null);
            case "acknowledgeSourceChange":
                triage.SourceReviewFlag = Track(changes, "sourceReviewFlag", triage.SourceReviewFlag, null);
                return ("sourceChangeAcknowledged", decision.Note);
            default:
                throw new InvalidOperationException("Unsupported issue decision.");
        }

        void Move(string status)
        {
            triage.Status = Track(changes, "status", triage.Status, status);
            triage.EnteredReviewAt = status == "needsReview" ? now : null;
        }

        void ReturnToReview()
        {
            triage.AssigneeId = Track(changes, "assigneeId", triage.AssigneeId, null);
            triage.Question = Track(changes, "question", triage.Question, null);
            triage.DeferralReason = Track(changes, "deferralReason", triage.DeferralReason, null);
            Move("needsReview");
        }
    }

    internal static T Track<T>(List<ActivityChange> changes, string field, T before, T after)
    {
        if (!EqualityComparer<T>.Default.Equals(before, after)) changes.Add(new(field, before, after));
        return after;
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
