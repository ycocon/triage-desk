using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TriageDesk.Api.Modules.IdentityAndWorkspaces;
using TriageDesk.Api.Modules.RepositoriesAndImport;
using TriageDesk.Api.Modules.TriageAndActivity;
using TriageDesk.Api.Persistence;
using Xunit;

namespace TriageDesk.Api.IntegrationTests;

public sealed class HandoffTests
{
    [Fact]
    public async Task ClaimsSerializeAndReplaysPreserveOneHistoryEntryAndOriginalSource()
    {
        using var host = new WebApplicationFactory<Program>();
        using var owner = await SignIn(host);
        using var reviewer = await SignIn(host);
        using var first = await SignIn(host);
        using var second = await SignIn(host);
        var created = await Expect(owner, HttpMethod.Post, "/workspaces", new { name = "Triage handoff" }, HttpStatusCode.Created);
        var workspace = created.GetProperty("workspaceId").GetGuid();
        var (completedIssue, activeIssue) = await Seed(host, workspace, reviewer.Id, first.Id, second.Id);
        var space = $"/workspaces/{workspace}";
        var issue = space + $"/issues/{completedIssue}";

        var accept = new { kind = "accept", commandId = Guid.NewGuid(), expectedVersion = 1,
            priority = "high", nextStep = "Reproduce on Windows." };
        await Expect(first, HttpMethod.Post, issue + "/commands", accept, HttpStatusCode.Forbidden);
        var ready = await Expect(reviewer, HttpMethod.Post, issue + "/commands", accept, HttpStatusCode.OK);
        Assert.Equal("ready", ready.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, ready.GetProperty("assigneeId").ValueKind);
        Assert.Equal(2, ready.GetProperty("decisionVersion").GetInt32());
        await Expect(reviewer, HttpMethod.Post, issue + "/commands", accept, HttpStatusCode.OK);

        var firstClaim = new { commandId = Guid.NewGuid(), expectedVersion = 2 };
        var secondClaim = new { commandId = Guid.NewGuid(), expectedVersion = 2 };
        var claims = await Task.WhenAll(Send(first, HttpMethod.Post, issue + "/claim", firstClaim),
            Send(second, HttpMethod.Post, issue + "/claim", secondClaim));
        var firstWon = claims[0].Status == HttpStatusCode.OK;
        var winner = firstWon ? first : second;
        var loser = firstWon ? second : first;
        var winningClaim = firstWon ? firstClaim : secondClaim;
        var winningResult = firstWon ? claims[0] : claims[1];
        var losingResult = firstWon ? claims[1] : claims[0];
        Assert.Equal(HttpStatusCode.OK, winningResult.Status);
        Assert.Equal(HttpStatusCode.Conflict, losingResult.Status);
        Assert.Equal("already_claimed", losingResult.Body.GetProperty("code").GetString());
        Assert.Equal(winner.Id, losingResult.Body.GetProperty("currentTriage").GetProperty("assigneeId").GetGuid());
        Assert.Equal("ready", winningResult.Body.GetProperty("status").GetString());
        var replay = await Expect(winner, HttpMethod.Post, issue + "/claim", winningClaim, HttpStatusCode.OK);
        Assert.Equal(3, replay.GetProperty("decisionVersion").GetInt32());

        var reused = await Expect(winner, HttpMethod.Post, issue + "/claim",
            new { winningClaim.commandId, expectedVersion = 3 }, HttpStatusCode.Conflict);
        Assert.Equal("command_id_reused", reused.GetProperty("code").GetString());
        var start = new { kind = "start", commandId = Guid.NewGuid(), expectedVersion = 3 };
        await Expect(loser, HttpMethod.Post, issue + "/commands", start, HttpStatusCode.Forbidden);
        await Expect(reviewer, HttpMethod.Post, issue + "/commands", start, HttpStatusCode.Forbidden);
        var stale = await Expect(winner, HttpMethod.Post, issue + "/commands",
            new { start.kind, start.commandId, expectedVersion = 2 }, HttpStatusCode.Conflict);
        Assert.Equal("stale_decision", stale.GetProperty("code").GetString());
        await Expect(winner, HttpMethod.Post, issue + "/commands", start, HttpStatusCode.OK);
        var complete = new { kind = "complete", commandId = Guid.NewGuid(), expectedVersion = 4 };
        await Expect(winner, HttpMethod.Post, issue + "/commands", complete, HttpStatusCode.OK);
        await Expect(winner, HttpMethod.Post, issue + "/commands", complete, HttpStatusCode.OK);

        var myWork = await Expect(winner, HttpMethod.Get, space + "/issues?view=myWork", null, HttpStatusCode.OK);
        Assert.Equal(0, myWork.GetProperty("total").GetInt32());
        var done = await Expect(reviewer, HttpMethod.Get, space + "/issues?view=all&status=done", null, HttpStatusCode.OK);
        Assert.Equal(1, done.GetProperty("total").GetInt32());
        var detail = await Expect(reviewer, HttpMethod.Get, issue, null, HttpStatusCode.OK);
        Assert.Equal("open", detail.GetProperty("source").GetProperty("state").GetString());
        Assert.Equal("done", detail.GetProperty("triage").GetProperty("status").GetString());
        var history = await Expect(reviewer, HttpMethod.Get, issue + "/activity", null, HttpStatusCode.OK);
        var entries = history.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(4, entries.Length);
        Assert.Equal("completed", entries[0].GetProperty("kind").GetString());
        Assert.Equal(winner.Id, entries[0].GetProperty("actorId").GetGuid());
        Assert.NotNull(entries[0].GetProperty("actorName").GetString());
        Assert.Single(entries, e => e.GetProperty("kind").GetString() == "claimed");

        var active = space + $"/issues/{activeIssue}";
        await Expect(reviewer, HttpMethod.Post, active + "/commands",
            new { accept.kind, commandId = Guid.NewGuid(), accept.expectedVersion, accept.priority, accept.nextStep }, HttpStatusCode.OK);
        await Expect(winner, HttpMethod.Post, active + "/claim",
            new { commandId = Guid.NewGuid(), expectedVersion = 2 }, HttpStatusCode.OK);
        await Expect(owner, HttpMethod.Delete, space + "/members/" + winner.Id, null, HttpStatusCode.NoContent);
        var returned = await Expect(reviewer, HttpMethod.Get, active, null, HttpStatusCode.OK);
        Assert.Equal("needsReview", returned.GetProperty("triage").GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, returned.GetProperty("triage").GetProperty("assigneeId").ValueKind);
        var retiredHistory = await Expect(reviewer, HttpMethod.Get, active + "/activity", null, HttpStatusCode.OK);
        Assert.Equal("assignmentReturned", retiredHistory.GetProperty("items")[0].GetProperty("kind").GetString());
        var preserved = await Expect(reviewer, HttpMethod.Get, issue, null, HttpStatusCode.OK);
        Assert.Equal(winner.Id, preserved.GetProperty("triage").GetProperty("assigneeId").GetGuid());
        await Expect(winner, HttpMethod.Post, issue + "/claim", winningClaim, HttpStatusCode.NotFound);
    }

    private static async Task<(Guid, Guid)> Seed(WebApplicationFactory<Program> host, Guid workspace,
        Guid reviewer, Guid first, Guid second)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TriageDeskDbContext>();
        var now = DateTimeOffset.UtcNow;
        db.WorkspaceMembers.AddRange(new[] { (reviewer, "reviewer"), (first, "member"), (second, "member") }
            .Select(member => new WorkspaceMember { WorkspaceId = workspace, UserId = member.Item1,
                BaseRole = member.Item2, JoinedAt = now, UpdatedAt = now }));
        var repo = new RepositoryConnection { WorkspaceId = workspace, Id = Guid.CreateVersion7(),
            GitHubId = 990, FullName = "northline/cli", ConnectedAt = now };
        db.Repositories.Add(repo);
        var issues = new[] { 42, 43 }.Select(number => new IssueSnapshot
        {
            WorkspaceId = workspace, Id = Guid.CreateVersion7(), RepositoryId = repo.Id, GitHubId = number,
            Number = number, Title = "Issue " + number, SourceCreatedAt = now, SourceUpdatedAt = now, ObservedAt = now
        }).ToArray();
        db.IssueSnapshots.AddRange(issues);
        db.IssueTriage.AddRange(issues.Select(i => new IssueTriage
        {
            WorkspaceId = workspace, IssueId = i.Id, EnteredReviewAt = now, UpdatedAt = now
        }));
        await db.SaveChangesAsync();
        return (issues[0].Id, issues[1].Id);
    }

    private static async Task<Session> SignIn(WebApplicationFactory<Program> host)
    {
        using var scope = host.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<Account>>();
        var email = Guid.NewGuid() + "@example.test";
        const string password = "local test password 2026";
        var id = Guid.CreateVersion7();
        Assert.True((await users.CreateAsync(new Account
        {
            Id = id, Email = email, UserName = email, DisplayName = "Handoff teammate", EmailConfirmed = true
        }, password)).Succeeded);
        var client = host.CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        var session = new Session(id, client);
        await Expect(session, HttpMethod.Post, "/auth/login", new { email, password }, HttpStatusCode.NoContent);
        return session;
    }

    private static async Task<JsonElement> Expect(Session session, HttpMethod method, string path, object? body, HttpStatusCode status)
    {
        var reply = await Send(session, method, path, body);
        Assert.True(reply.Status == status, $"{method} {path} expected {status}, got {reply.Status}: {reply.Body}");
        return reply.Body;
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> Send(Session session, HttpMethod method, string path, object? body)
    {
        using var request = new HttpRequestMessage(method, "/api/v1" + path);
        if (method != HttpMethod.Get)
        {
            var csrf = await session.Client.GetFromJsonAsync<JsonElement>("/api/v1/auth/csrf");
            request.Headers.Add("X-CSRF-Token", csrf.GetProperty("requestToken").GetString());
        }
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await session.Client.SendAsync(request);
        var content = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, content.Length == 0 ? default : JsonDocument.Parse(content).RootElement.Clone());
    }

    private sealed record Session(Guid Id, HttpClient Client) : IDisposable
    {
        public void Dispose() => Client.Dispose();
    }
}
