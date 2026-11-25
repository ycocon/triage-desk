using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TriageDesk.Api.Modules.TriageAndActivity;
using TriageDesk.Api.Persistence;
using Xunit;

namespace TriageDesk.Api.IntegrationTests;

public sealed partial class HandoffTests
{
    [Fact]
    public async Task ReviewerDecisionsAndAssignedAnswersEnforceRolesAndTransitions()
    {
        using var host = new WebApplicationFactory<Program>();
        using var owner = await SignIn(host);
        using var reviewer = await SignIn(host);
        using var first = await SignIn(host);
        using var second = await SignIn(host);
        var workspace = (await Expect(owner, HttpMethod.Post, "/workspaces", new { name = "Advanced triage" },
            HttpStatusCode.Created)).GetProperty("workspaceId").GetGuid();
        var (bugId, featureId) = await Seed(host, workspace, reviewer.Id, first.Id, second.Id);
        using (var scope = host.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<TriageDeskDbContext>();
            (await store.IssueSnapshots.FindAsync([workspace, bugId]))!.Labels = ["BUG"];
            (await store.IssueSnapshots.FindAsync([workspace, featureId]))!.Labels = ["bugfix"];
            await store.SaveChangesAsync();
        }
        var bug = $"/workspaces/{workspace}/issues/{bugId}";
        var feature = $"/workspaces/{workspace}/issues/{featureId}";

        const string question = "Which Windows version shows the crash?";
        const string answer = "Windows 11 on a clean install.";
        await Reject(first, bug, "requestInformation", 1, HttpStatusCode.Forbidden,
            ("assigneeId", first.Id), ("question", question));
        await Reject(first, bug, "setAside", 1, HttpStatusCode.Forbidden, ("reason", "Duplicate"));
        await Reject(reviewer, bug, "returnToReview", 1, HttpStatusCode.Conflict);
        await Reject(reviewer, bug, "reassign", 1, HttpStatusCode.Conflict, ("assigneeId", second.Id));
        await Reject(reviewer, bug, "changePriority", 1, HttpStatusCode.Conflict, ("priority", "high"));
        await Reject(reviewer, bug, "acknowledgeSourceChange", 1, HttpStatusCode.Conflict);
        var nonBug = await Reject(reviewer, feature, "changeSeverity", 1, HttpStatusCode.Conflict, ("severity", "major"));
        Assert.Equal("severity_requires_bug", nonBug.GetProperty("code").GetString());
        Assert.Equal(0, await CountHistory(reviewer, bug));
        Assert.Equal(0, await CountHistory(reviewer, feature));

        var waiting = await Decide(reviewer, bug, "requestInformation", 1, ("assigneeId", first.Id), ("question", question));
        Assert.Equal("waitingForInformation", waiting.GetProperty("status").GetString());
        Assert.Equal(question, waiting.GetProperty("question").GetString());
        await Reject(reviewer, bug, "reassign", 2, HttpStatusCode.Conflict, ("assigneeId", null));
        await Decide(reviewer, bug, "reassign", 2, ("assigneeId", second.Id));
        await Reject(first, bug, "answer", 3, HttpStatusCode.Forbidden, ("answer", answer));
        await Reject(reviewer, bug, "answer", 3, HttpStatusCode.Forbidden, ("answer", answer));
        var command = new { kind = "answer", commandId = Guid.NewGuid(), expectedVersion = 3, answer };
        var answered = await Expect(second, HttpMethod.Post, bug + "/commands", command, HttpStatusCode.OK);
        Assert.Equal("needsReview", answered.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, answered.GetProperty("assigneeId").ValueKind);
        Assert.Equal(JsonValueKind.Null, answered.GetProperty("question").ValueKind);
        Assert.Equal(JsonValueKind.String, answered.GetProperty("enteredReviewAt").ValueKind);
        var replay = await Expect(second, HttpMethod.Post, bug + "/commands", command, HttpStatusCode.OK);
        Assert.Equal(4, replay.GetProperty("decisionVersion").GetInt32());
        var history = await Expect(reviewer, HttpMethod.Get, bug + "/activity", null, HttpStatusCode.OK);
        Assert.Equal(3, history.GetProperty("total").GetInt32());
        var answerEntry = history.GetProperty("items")[0].GetProperty("changes").EnumerateArray().ToArray();
        Assert.Contains(answerEntry, change => change.GetProperty("field").GetString() == "question" &&
            change.GetProperty("before").GetString() == question);
        Assert.Contains(answerEntry, change => change.GetProperty("field").GetString() == "answer" &&
            change.GetProperty("after").GetString() == answer);

        var accepted = await Decide(reviewer, bug, "accept", 4, ("priority", "high"), ("nextStep", "Investigate"),
            ("assigneeId", first.Id), ("severity", "critical"));
        Assert.Equal(first.Id, accepted.GetProperty("assigneeId").GetGuid());
        Assert.Equal("critical", accepted.GetProperty("severity").GetString());
        var cleared = await Decide(reviewer, bug, "changeSeverity", 5, ("severity", null));
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("severity").ValueKind);
        await Decide(reviewer, bug, "changePriority", 6, ("priority", "low"));
        var stale = await Reject(first, bug, "editNextStep", 6, HttpStatusCode.Conflict, ("nextStep", "My draft"));
        Assert.Equal("stale_decision", stale.GetProperty("code").GetString());
        Assert.Equal("Investigate", stale.GetProperty("currentTriage").GetProperty("nextStep").GetString());
        Assert.Equal("low", stale.GetProperty("currentTriage").GetProperty("priority").GetString());
        Assert.Equal(6, await CountHistory(reviewer, bug));
        await Reject(second, bug, "editNextStep", 7, HttpStatusCode.Forbidden, ("nextStep", "Someone else's assignment"));
        await Decide(first, bug, "editNextStep", 7, ("nextStep", "Reproduce with logs"));
        await Decide(first, bug, "start", 8);
        await Reject(reviewer, bug, "reassign", 9, HttpStatusCode.Conflict, ("assigneeId", null));
        await Decide(reviewer, bug, "reassign", 9, ("assigneeId", second.Id));
        await Decide(second, bug, "complete", 10);
        var reopened = await Decide(reviewer, bug, "returnToReview", 11, ("reason", "Regression reported"));
        Assert.Equal("needsReview", reopened.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, reopened.GetProperty("assigneeId").ValueKind);
        var rated = await Decide(reviewer, bug, "changeSeverity", 12, ("severity", "minor"));
        Assert.Equal("minor", rated.GetProperty("severity").GetString());
        var unrated = await Decide(reviewer, bug, "accept", 13,
            ("priority", "normal"), ("nextStep", "Investigate again"), ("severity", null));
        Assert.Equal(JsonValueKind.Null, unrated.GetProperty("severity").ValueKind);

        await Reject(reviewer, feature, "setAside", 1, HttpStatusCode.BadRequest, ("reason", " "));
        var deferred = await Decide(reviewer, feature, "setAside", 1, ("reason", "Needs a product decision"));
        Assert.Equal("setAside", deferred.GetProperty("status").GetString());
        var readyAgain = await Decide(reviewer, feature, "returnToReview", 2);
        Assert.Equal("needsReview", readyAgain.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, readyAgain.GetProperty("deferralReason").ValueKind);
        var deferrals = await Expect(reviewer, HttpMethod.Get, feature + "/activity", null, HttpStatusCode.OK);
        Assert.Equal("Needs a product decision", deferrals.GetProperty("items")[1].GetProperty("reason").GetString());

        using (var scope = host.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<TriageDeskDbContext>();
            (await store.IssueTriage.FindAsync([workspace, featureId]))!.SourceReviewFlag = "reopenedAfterDecision";
            await store.SaveChangesAsync();
        }
        var acknowledged = await Decide(reviewer, feature, "acknowledgeSourceChange", 3);
        Assert.Equal(JsonValueKind.Null, acknowledged.GetProperty("sourceReviewFlag").ValueKind);
        Assert.Equal("needsReview", acknowledged.GetProperty("status").GetString());
    }

    [Fact]
    public async Task OldReceiptsAreStillReplayedAndClaimCannotOutliveMembership()
    {
        using var host = new WebApplicationFactory<Program>();
        using var owner = await SignIn(host);
        using var reviewer = await SignIn(host);
        using var first = await SignIn(host);
        using var second = await SignIn(host);
        var workspace = (await Expect(owner, HttpMethod.Post, "/workspaces", new { name = "Replay compatibility" },
            HttpStatusCode.Created)).GetProperty("workspaceId").GetGuid();
        var (firstIssue, _) = await Seed(host, workspace, reviewer.Id, first.Id, second.Id);
        var path = $"/workspaces/{workspace}";
        var issue = path + $"/issues/{firstIssue}";
        var commandId = Guid.NewGuid();
        const string step = "Investigate the report";
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TriageDeskDbContext>();
            var triage = (await db.IssueTriage.FindAsync([workspace, firstIssue]))!;
            triage.Status = "ready";
            triage.DecisionVersion = 2;
            triage.Priority = "normal";
            triage.NextStep = step;
            triage.EnteredReviewAt = null;
            var oldPayload = new { Kind = "accept", Id = commandId, Version = 1, Priority = "normal",
                NextStep = step, Assignee = (Guid?)null };
            db.IssueActivity.Add(new IssueActivity
            {
                WorkspaceId = workspace, Id = Guid.CreateVersion7(), IssueId = firstIssue, ActorId = reviewer.Id,
                OperationId = commandId, Kind = "accepted", OccurredAt = DateTimeOffset.UtcNow,
                Changes = JsonSerializer.SerializeToElement(Array.Empty<string>()),
                CommandDigest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(oldPayload)))
            });
            await db.SaveChangesAsync();
        }
        var replay = await Expect(reviewer, HttpMethod.Post, issue + "/commands",
            new { kind = "accept", commandId, expectedVersion = 1, priority = "normal", nextStep = step }, HttpStatusCode.OK);
        Assert.Equal(2, replay.GetProperty("decisionVersion").GetInt32());
        Assert.Equal(1, await CountHistory(reviewer, issue));

        var actions = await Task.WhenAll(
            Send(first, HttpMethod.Post, issue + "/claim", new { commandId = Guid.NewGuid(), expectedVersion = 2 }),
            Send(owner, HttpMethod.Delete, path + "/members/" + first.Id, null));
        Assert.Equal(HttpStatusCode.NoContent, actions[1].Status);
        Assert.Contains(actions[0].Status, new[] { HttpStatusCode.OK, HttpStatusCode.NotFound });
        var current = (await Expect(reviewer, HttpMethod.Get, issue, null, HttpStatusCode.OK)).GetProperty("triage");
        Assert.Equal(JsonValueKind.Null, current.GetProperty("assigneeId").ValueKind);
        var claimedFirst = actions[0].Status == HttpStatusCode.OK;
        Assert.Equal(claimedFirst ? "needsReview" : "ready", current.GetProperty("status").GetString());
        Assert.Equal(claimedFirst ? 3 : 1, await CountHistory(reviewer, issue));
    }

    private static Task<JsonElement> Decide(Session actor, string issue, string kind, int version,
        params (string Field, object? Value)[] fields) => Reject(actor, issue, kind, version, HttpStatusCode.OK, fields);

    private static Task<JsonElement> Reject(Session actor, string issue, string kind, int version, HttpStatusCode status,
        params (string Field, object? Value)[] fields)
    {
        var command = new Dictionary<string, object?> { ["kind"] = kind, ["commandId"] = Guid.NewGuid(), ["expectedVersion"] = version };
        foreach (var (field, value) in fields) command[field] = value;
        return Expect(actor, HttpMethod.Post, issue + "/commands", command, status);
    }

    private static async Task<int> CountHistory(Session actor, string issue) =>
        (await Expect(actor, HttpMethod.Get, issue + "/activity", null, HttpStatusCode.OK)).GetProperty("total").GetInt32();
}
