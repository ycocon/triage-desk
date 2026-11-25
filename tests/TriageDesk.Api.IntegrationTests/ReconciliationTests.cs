using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TriageDesk.Api.Persistence;
using Xunit;

namespace TriageDesk.Api.IntegrationTests;

public sealed partial class RepositoryImportTests
{
    [Fact]
    public async Task IncrementalRunsPreserveWatermarkThroughQuotaAndTimeoutAndConfirmVisibility()
    {
        var clock = new ImportClock();
        var github = new ReconciliationFixture(clock);
        using var host = CreateReconciliationHost(github, clock);
        using var owner = await SignIn(host);
        var path = await Workspace(owner);
        var repo = await Connect(owner, path, "acme/source");

        github.List = (_, _) => [github.Report(901, 42), github.Report(902, 88)];
        await Finish(owner, await Begin(owner, repo));
        var initial = await Repository(owner, repo);
        var watermark = initial.GetProperty("lastSuccessAt").GetDateTimeOffset();
        var first = await Issue(owner, path, 42);
        var missing = await Issue(owner, path, 88);
        await Command(owner, first, "accept", 1, new { priority = "high", nextStep = "Reproduce",
            assigneeId = (await Send(owner, HttpMethod.Get, "/me", null, HttpStatusCode.OK)).GetProperty("userId").GetGuid() });

        clock.Now += TimeSpan.FromMinutes(10);
        github.List = (_, _) => [github.Report(901, 42, "closed"), github.Report(977, 77, "closed"),
            github.Report(999, 99, updatedAt: clock.Now.AddMinutes(1))];
        var incremental = await Begin(owner, repo);
        github.ThrottleNextList = true;
        var quota = await AdvanceReconciliation(owner, incremental, 0, HttpStatusCode.TooManyRequests);
        Assert.Equal("rate_limited", quota.GetProperty("code").GetString());
        var saved = await RunState(owner, incremental);
        Assert.Equal("paused", saved.GetProperty("state").GetString());
        Assert.Equal(0, saved.GetProperty("checkpoint").GetInt32());
        var calls = github.Calls;
        await Send(owner, HttpMethod.Post, path + "/repository-previews", new { fullName = "acme/destination" },
            HttpStatusCode.TooManyRequests);
        Assert.Equal(calls, github.Calls);
        clock.Now += TimeSpan.FromSeconds(16);

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        github.ListEntered = entered;
        github.ReleaseList = release;
        var active = AdvanceReconciliation(owner, incremental, 0);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(6));
        try
        {
            var competing = await AdvanceReconciliation(owner, incremental, 0, HttpStatusCode.Conflict);
            Assert.Equal("run_busy", competing.GetProperty("code").GetString());
        }
        finally { release.TrySetResult(); }
        var listing = await active;
        Assert.Equal("running", listing.GetProperty("state").GetString());
        Assert.Equal(1, listing.GetProperty("checkpoint").GetInt32());
        Assert.Equal("all", github.LastState);
        Assert.Equal(watermark.AddMinutes(-5).ToUnixTimeSeconds(), github.LastSince!.Value.ToUnixTimeSeconds());
        Assert.Equal(watermark, (await Repository(owner, repo)).GetProperty("lastSuccessAt").GetDateTimeOffset());

        var closed = await Detail(owner, first);
        var triage = closed.GetProperty("triage");
        Assert.Equal("closed", closed.GetProperty("source").GetProperty("state").GetString());
        Assert.Equal("closedWhileActive", triage.GetProperty("sourceReviewFlag").GetString());
        Assert.Equal("ready", triage.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, triage.GetProperty("assigneeId").ValueKind);
        Assert.Equal(JsonValueKind.Object, (await Detail(owner, missing)).GetProperty("source").ValueKind);

        var checkCalls = github.Calls;
        await AdvanceReconciliation(owner, incremental, 0);
        Assert.Equal(checkCalls, github.Calls);
        github.FailCanonicalWithTimeout = true;
        await AdvanceReconciliation(owner, incremental, 1, HttpStatusCode.BadGateway);
        Assert.Equal(JsonValueKind.Object, (await Detail(owner, missing)).GetProperty("source").ValueKind);
        Assert.Equal(watermark, (await Repository(owner, repo)).GetProperty("lastSuccessAt").GetDateTimeOffset());
        clock.Now += TimeSpan.FromSeconds(3);
        github.FailCanonicalWithTimeout = false;
        var finished = await AdvanceReconciliation(owner, incremental, 1);
        Assert.Equal("succeeded", finished.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, (await Detail(owner, missing)).GetProperty("source").ValueKind);
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TriageDeskDbContext>();
            var missingId = Guid.Parse(missing.Split('/')[^1]);
            Assert.True(await db.IssueSnapshots.AnyAsync(i => i.Id == missingId &&
                !i.SourceAvailable && i.Title == "" && i.Body == null && i.Labels.Length == 0));
        }

        await Command(owner, first, "start", 3);
        await Command(owner, first, "complete", 4);
        clock.Now += TimeSpan.FromMinutes(10);
        github.List = (_, _) => [github.Report(901, 42)];
        await Finish(owner, await Begin(owner, repo));
        Assert.Equal(saved.GetProperty("startedAt").GetDateTimeOffset().AddMinutes(-5).ToUnixTimeSeconds(),
            github.LastSince!.Value.ToUnixTimeSeconds());
        var reopened = await Detail(owner, first);
        Assert.Equal("reopenedAfterDecision", reopened.GetProperty("triage").GetProperty("sourceReviewFlag").GetString());
        Assert.Equal("done", reopened.GetProperty("triage").GetProperty("status").GetString());
        var history = await Send(owner, HttpMethod.Get, first + "/activity", null, HttpStatusCode.OK);
        Assert.Equal(2, history.GetProperty("items").EnumerateArray().Count(item =>
            item.GetProperty("kind").GetString() == "sourceStateChanged"));

        var interrupted = await Begin(owner, repo);
        entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        github.ListEntered = entered;
        github.ReleaseList = release;
        var inFlight = AdvanceReconciliation(owner, interrupted, 0, HttpStatusCode.Conflict);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(6));
        try { await Send(owner, HttpMethod.Delete, repo, null, HttpStatusCode.NoContent); }
        finally { release.TrySetResult(); }
        Assert.Equal("repository_disconnected", (await inFlight).GetProperty("code").GetString());
        Assert.Equal("cancelled", (await RunState(owner, interrupted)).GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, (await Detail(owner, first)).GetProperty("source").ValueKind);
        var reconnected = await Send(owner, HttpMethod.Post, path + "/repositories",
            new { fullName = "acme/source" }, HttpStatusCode.OK);
        Assert.EndsWith(reconnected.GetProperty("repositoryId").GetGuid().ToString(), repo);
        Assert.Equal(JsonValueKind.Null, (await Detail(owner, first)).GetProperty("source").ValueKind);
        clock.Now += TimeSpan.FromMinutes(1);
        await Finish(owner, await Begin(owner, repo));
        Assert.Equal("open", github.LastState);
        Assert.Equal("done", (await Detail(owner, first)).GetProperty("triage").GetProperty("status").GetString());

        clock.Now += TimeSpan.FromMinutes(1);
        github.SourceUnavailable = true;
        var denied = await Begin(owner, repo);
        await AdvanceReconciliation(owner, denied, 0, HttpStatusCode.BadGateway);
        Assert.Equal("failed", (await RunState(owner, denied)).GetProperty("state").GetString());
        Assert.Equal("unavailable", (await Repository(owner, repo)).GetProperty("visibility").GetString());
        Assert.Equal(JsonValueKind.Null, (await Detail(owner, first)).GetProperty("source").ValueKind);
        Assert.Equal("done", (await Detail(owner, first)).GetProperty("triage").GetProperty("status").GetString());
    }

    [Fact]
    public async Task ConfirmedLossScrubsOtherWorkspacesAndFencesOlderReads()
    {
        var clock = new ImportClock();
        var github = new ReconciliationFixture(clock);
        using var host = CreateReconciliationHost(github, clock);
        using var owner = await SignIn(host);
        var firstWorkspace = await Workspace(owner);
        var secondWorkspace = await Workspace(owner);
        var firstRepo = await Connect(owner, firstWorkspace, "acme/source");
        var secondRepo = await Connect(owner, secondWorkspace, "acme/source");
        github.List = (_, _) => [github.Report(901, 42), github.Report(902, 88)];
        await Finish(owner, await Begin(owner, firstRepo));
        await Finish(owner, await Begin(owner, secondRepo));
        var secondIssue = await Issue(owner, secondWorkspace, 88);
        await Command(owner, secondIssue, "setAside", 1, new { reason = "Retain this reasoning" });

        clock.Now += TimeSpan.FromMinutes(1);
        var otherRun = await Begin(owner, secondRepo);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        github.ListEntered = entered;
        github.ReleaseList = release;
        var staleRead = AdvanceReconciliation(owner, otherRun, 0, HttpStatusCode.BadGateway);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(6));
        try
        {
            clock.Now += TimeSpan.FromSeconds(5);
            github.List = (_, _) => [github.Report(901, 42)];
            await Finish(owner, await Begin(owner, firstRepo));
            var hidden = await Detail(owner, secondIssue);
            Assert.Equal(JsonValueKind.Null, hidden.GetProperty("source").ValueKind);
            Assert.Equal("Retain this reasoning", hidden.GetProperty("triage").GetProperty("deferralReason").GetString());
        }
        finally { release.TrySetResult(); }
        Assert.Equal("upstream_error", (await staleRead).GetProperty("code").GetString());
        Assert.Equal(0, (await RunState(owner, otherRun)).GetProperty("checkpoint").GetInt32());
        Assert.Equal(JsonValueKind.Null, (await Detail(owner, secondIssue)).GetProperty("source").ValueKind);

        clock.Now += TimeSpan.FromMinutes(1);
        github.SourceUnavailable = true;
        await AdvanceReconciliation(owner, await Begin(owner, firstRepo), 0, HttpStatusCode.BadGateway);
        Assert.Equal("failed", (await RunState(owner, otherRun)).GetProperty("state").GetString());
        Assert.Equal("unavailable", (await Repository(owner, secondRepo)).GetProperty("visibility").GetString());
        var secondWorkspaceId = Guid.Parse(secondWorkspace.Split('/')[^1]);
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TriageDeskDbContext>();
        Assert.Equal(2, await db.IssueSnapshots.CountAsync(issue => issue.WorkspaceId == secondWorkspaceId &&
            !issue.SourceAvailable && issue.Title == "" && issue.Body == null && issue.Labels.Length == 0));
    }

    [Fact]
    public async Task VerifiedTransferPreservesLocalIdentityAndConflictPreservesBothHistories()
    {
        var clock = new ImportClock();
        var github = new ReconciliationFixture(clock);
        using var host = CreateReconciliationHost(github, clock);
        using var owner = await SignIn(host);
        var path = await Workspace(owner);
        var source = await Connect(owner, path, "acme/source");
        var destination = await Connect(owner, path, "acme/destination");
        github.List = (slug, _) => slug == "source"
            ? [github.Report(901, 41), github.Report(902, 42)]
            : [github.Report(990, 8, repo: "destination")];
        await Finish(owner, await Begin(owner, source));
        await Finish(owner, await Begin(owner, destination));
        var moved = await Issue(owner, path, 41);
        var conflictingSource = await Issue(owner, path, 42);
        var conflictingTarget = await Issue(owner, path, 8);

        await Command(owner, moved, "accept", 1, new { priority = "high", nextStep = "Keep original decision" });
        await Command(owner, conflictingSource, "setAside", 1, new { reason = "Original reason" });
        await Command(owner, conflictingTarget, "accept", 1, new { priority = "low", nextStep = "Keep separate decision" });

        clock.Now += TimeSpan.FromMinutes(3);
        github.List = (_, _) => [];
        github.RedirectIssues = true;
        await Finish(owner, await Begin(owner, source));
        var transferred = await Detail(owner, moved);
        Assert.Equal("991", transferred.GetProperty("source").GetProperty("githubIssueId").GetString());
        Assert.Equal(9, transferred.GetProperty("source").GetProperty("number").GetInt32());
        Assert.EndsWith(transferred.GetProperty("repositoryId").GetGuid().ToString(), destination);
        Assert.Equal("Keep original decision", transferred.GetProperty("triage").GetProperty("nextStep").GetString());
        Assert.Equal(moved, await Issue(owner, path, 9));

        var conflictOne = (await Detail(owner, conflictingSource)).GetProperty("triage");
        var conflictTwo = (await Detail(owner, conflictingTarget)).GetProperty("triage");
        Assert.Equal("transferConflict", conflictOne.GetProperty("sourceReviewFlag").GetString());
        Assert.Equal("transferConflict", conflictTwo.GetProperty("sourceReviewFlag").GetString());
        Assert.Equal("Original reason", conflictOne.GetProperty("deferralReason").GetString());
        Assert.Equal("Keep separate decision", conflictTwo.GetProperty("nextStep").GetString());

        clock.Now += TimeSpan.FromMinutes(1);
        github.List = (_, _) => [github.Report(901, 41)];
        await Finish(owner, await Begin(owner, source));
        Assert.Equal(9, (await Detail(owner, moved)).GetProperty("source").GetProperty("number").GetInt32());
        foreach (var issue in new[] { moved, conflictingSource, conflictingTarget })
            Assert.Equal(2, (await Send(owner, HttpMethod.Get, issue + "/activity", null, HttpStatusCode.OK))
                .GetProperty("total").GetInt32());

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TriageDeskDbContext>();
        var movedId = Guid.Parse(moved.Split('/')[^1]);
        var aliases = await db.IssueExternalKeys.Where(k => k.IssueId == movedId).ToListAsync();
        Assert.Contains(aliases, k => k.GitHubId == 901 && !k.Current);
        Assert.Contains(aliases, k => k.GitHubId == 991 && k.Current);
    }

    private static async Task<string> Workspace(HttpClient owner)
    {
        var result = await Send(owner, HttpMethod.Post, "/workspaces", new { name = "Reconciliation tests" }, HttpStatusCode.Created);
        return "/workspaces/" + result.GetProperty("workspaceId").GetGuid();
    }

    private static async Task<string> Connect(HttpClient owner, string workspace, string fullName)
    {
        var connection = await Send(owner, HttpMethod.Post, workspace + "/repositories", new { fullName }, HttpStatusCode.Created);
        return workspace + "/repositories/" + connection.GetProperty("repositoryId").GetGuid();
    }

    private static async Task<string> Begin(HttpClient owner, string repo)
    {
        var id = Guid.NewGuid();
        await Send(owner, HttpMethod.Post, repo + "/sync-runs", new { runId = id }, HttpStatusCode.Created);
        return repo + "/sync-runs/" + id;
    }

    private static async Task Finish(HttpClient owner, string run)
    {
        for (var checkpoint = 0; checkpoint < 10; checkpoint++)
        {
            var progress = await AdvanceReconciliation(owner, run, checkpoint);
            if (progress.GetProperty("state").GetString() == "succeeded") return;
            Assert.Equal(checkpoint + 1, progress.GetProperty("checkpoint").GetInt32());
        }
        Assert.Fail("The test run did not finish.");
    }

    private static Task<JsonElement> AdvanceReconciliation(HttpClient owner, string run, int checkpoint,
        HttpStatusCode expected = HttpStatusCode.OK) =>
        Send(owner, HttpMethod.Post, run + "/advance", new { expectedCheckpoint = checkpoint }, expected);

    private static Task<JsonElement> RunState(HttpClient owner, string run) => Repository(owner, run);
    private static Task<JsonElement> Repository(HttpClient owner, string path) =>
        Send(owner, HttpMethod.Get, path, null, HttpStatusCode.OK);
    private static Task<JsonElement> Detail(HttpClient owner, string path) => Repository(owner, path);

    private static async Task<string> Issue(HttpClient owner, string workspace, int number)
    {
        var page = await Repository(owner, workspace + "/issues");
        var issue = page.GetProperty("items").EnumerateArray().Single(i =>
            i.GetProperty("source").GetProperty("number").GetInt32() == number);
        return workspace + "/issues/" + issue.GetProperty("issueId").GetGuid();
    }

    private static async Task Command(HttpClient owner, string issue, string kind, int version, object? fields = null)
    {
        var body = new Dictionary<string, object?> { ["kind"] = kind, ["commandId"] = Guid.NewGuid(), ["expectedVersion"] = version };
        if (fields is not null)
            foreach (var field in JsonSerializer.SerializeToElement(fields).EnumerateObject()) body[field.Name] = field.Value;
        await Send(owner, HttpMethod.Post, issue + "/commands", body, HttpStatusCode.OK);
    }

    private static WebApplicationFactory<Program> CreateReconciliationHost(ReconciliationFixture github, ImportClock clock) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.AddHttpClient("GitHubIssuesClient").ConfigurePrimaryHttpMessageHandler(() => github);
            services.AddSingleton<TimeProvider>(clock);
        }));

    private sealed class ReconciliationFixture(ImportClock clock) : HttpMessageHandler
    {
        private readonly long sourceId = Random.Shared.NextInt64(100_000_000, 10_000_000_000);
        public Func<string, string?, object[]> List { get; set; } = (_, _) => [];
        private int calls;
        public int Calls => Volatile.Read(ref calls);
        public bool ThrottleNextList { get; set; }
        public bool FailCanonicalWithTimeout { get; set; }
        public bool RedirectIssues { get; set; }
        public bool SourceUnavailable { get; set; }
        public TaskCompletionSource? ListEntered { get; set; }
        public TaskCompletionSource? ReleaseList { get; set; }
        public string? LastState { get; private set; }
        public DateTimeOffset? LastSince { get; private set; }

        internal object Report(int id, int number, string state = "open", string repo = "source", DateTimeOffset? updatedAt = null) =>
            new { id, number, state, title = "Report " + number, body = "Fixture report", labels = Array.Empty<object>(),
                repository_url = "https://api.github.com/repos/acme/" + repo, created_at = clock.Now.AddHours(-1),
                updated_at = updatedAt ?? clock.Now.AddMinutes(-1) };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref calls);
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("api.github.com", request.RequestUri!.Host);
            Assert.Null(request.Headers.Authorization);
            var path = request.RequestUri.AbsolutePath;
            if (path == "/repos/acme/source" || path == "/repositories/" + sourceId)
                return SourceUnavailable ? new(HttpStatusCode.NotFound) : Ok(new { id = sourceId, full_name = "acme/source", @private = false });
            if (path == "/repos/acme/destination") return Ok(new { id = sourceId + 1, full_name = "acme/destination", @private = false });

            if (path.EndsWith("/issues", StringComparison.Ordinal))
            {
                if (ThrottleNextList)
                {
                    ThrottleNextList = false;
                    var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                    limited.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(15));
                    return limited;
                }
                var query = QueryHelpers.ParseQuery(request.RequestUri.Query);
                LastState = query["state"][0];
                LastSince = query.TryGetValue("since", out var raw) &&
                    DateTimeOffset.TryParse(raw[0], out var since) ? since : null;
                var reports = List(path.Split('/')[3], LastState);
                if (ListEntered is { } entered && ReleaseList is { } release)
                {
                    ListEntered = null;
                    ReleaseList = null;
                    entered.TrySetResult();
                    await release.Task.WaitAsync(ct);
                }
                return Ok(reports);
            }
            if (path == "/repos/acme/source/issues/88")
                return FailCanonicalWithTimeout ? throw new TaskCanceledException("Fixture timeout") : new(HttpStatusCode.NotFound);
            if (RedirectIssues && path is "/repos/acme/source/issues/41" or "/repos/acme/source/issues/42")
            {
                var moved = new HttpResponseMessage(HttpStatusCode.MovedPermanently);
                moved.Headers.Location = new Uri("https://api.github.com/repos/acme/destination/issues/" + (path.EndsWith("41") ? 9 : 8));
                return moved;
            }
            if (path == "/repos/acme/destination/issues/9") return Ok(Report(991, 9, repo: "destination"));
            if (path == "/repos/acme/destination/issues/8") return Ok(Report(990, 8, repo: "destination"));
            return new(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Ok(object body) => new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
    }
}
