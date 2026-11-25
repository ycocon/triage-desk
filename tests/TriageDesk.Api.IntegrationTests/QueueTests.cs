using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using System.Text.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TriageDesk.Api.Modules.RepositoriesAndImport;
using TriageDesk.Api.Modules.TriageAndActivity;
using TriageDesk.Api.Persistence;
using Xunit;

namespace TriageDesk.Api.IntegrationTests;

public sealed partial class RepositoryImportTests
{
    [Fact]
    public void RuntimeRoutesMatchTheThirtyEightOperationContract()
    {
        using var host = CreateReconciliationHost(new ReconciliationFixture(new ImportClock()), new ImportClock());
        var endpoints = host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>();
        var actual = endpoints.Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/api/v1/") is true)
            .SelectMany(endpoint => (endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [])
                .Select(method => method + " " + Regex.Replace(endpoint.RoutePattern.RawText![7..].TrimEnd('/'), @":[^}]+", "")))
            .Order(StringComparer.Ordinal).ToArray();
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        string? contract = null;
        for (DirectoryInfo? current = directory; current is not null; current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, "contracts", "openapi", "v1.json");
            if (!File.Exists(candidate)) continue;
            contract = candidate;
            break;
        }
        Assert.NotNull(contract);
        using var document = JsonDocument.Parse(File.ReadAllText(contract));
        var expected = document.RootElement.GetProperty("paths").EnumerateObject()
            .SelectMany(path => path.Value.EnumerateObject().Where(op => op.Value.TryGetProperty("operationId", out _))
                .Select(op => op.Name.ToUpperInvariant() + " " + path.Name))
            .Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(38, expected.Length);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task QueueTotalsAndDrilldownsShareVisibilityAndFilters()
    {
        var clock = new ImportClock();
        using var host = CreateReconciliationHost(new ReconciliationFixture(clock), clock);
        using var owner = await SignIn(host);
        using var outsider = await SignIn(host);
        var path = await Workspace(owner);
        var otherPath = await Workspace(outsider);
        var workspaceId = Guid.Parse(path.Split('/')[^1]);
        var otherWorkspaceId = Guid.Parse(otherPath.Split('/')[^1]);
        var userId = (await Send(owner, HttpMethod.Get, "/me", null, HttpStatusCode.OK)).GetProperty("userId").GetGuid();
        Guid oldest;
        Guid assigned;
        Guid mainRepository;
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TriageDeskDbContext>();
            var first = QueueRepository(db, workspaceId, "team/cli", clock.Now);
            var second = QueueRepository(db, workspaceId, "labs/cli", clock.Now);
            var disconnected = QueueRepository(db, workspaceId, "team/old", clock.Now);
            disconnected.DisconnectedAt = clock.Now;
            var unavailable = QueueRepository(db, workspaceId, "team/private", clock.Now);
            unavailable.Public = false;
            var other = QueueRepository(db, otherWorkspaceId, "team/cli", clock.Now);
            mainRepository = first.Id;
            oldest = AddIssue(first, 42, "Crash with a 100% profile", "needsReview", when: clock.Now.AddDays(-2));
            AddIssue(second, 12, "Add examples", "needsReview");
            AddIssue(second, 42, "CLI crash in terminal", "ready", "high");
            assigned = AddIssue(first, 10, "Existing profile", "inProgress", "high", userId, state: "closed");
            AddIssue(first, 23, "Released profile", "done", "low", userId);
            var hidden = AddIssue(first, 50, "Secret title", "ready", "high", isVisible: false);
            AddIssue(disconnected, 13, "Secret title", "needsReview");
            AddIssue(unavailable, 14, "Secret title", "needsReview");
            AddIssue(other, 42, "Secret title", "needsReview");
            db.IssueActivity.Add(new IssueActivity
            {
                WorkspaceId = workspaceId, IssueId = hidden, Id = Guid.CreateVersion7(),
                OperationId = Guid.NewGuid(), Kind = "reassigned", OccurredAt = clock.Now.AddMinutes(-1),
                Changes = JsonSerializer.SerializeToElement(new[] {
                    new { field = "title", before = "Secret title", after = "Secret edit" },
                    new { field = "status", before = "needsReview", after = "ready" }
                })
            });
            await db.SaveChangesAsync();

            Guid AddIssue(RepositoryConnection repo, int number, string title, string status, string? priority = null,
                Guid? assignee = null, bool isVisible = true, DateTimeOffset? when = null, string state = "open")
            {
                var issue = new IssueSnapshot { WorkspaceId = repo.WorkspaceId, RepositoryId = repo.Id,
                    Id = Guid.CreateVersion7(), GitHubId = db.IssueSnapshots.Local.Count + 100, Number = number, Title = title,
                    State = state, SourceAvailable = isVisible, ObservedAt = clock.Now, SourceCreatedAt = clock.Now,
                    SourceUpdatedAt = clock.Now };
                db.IssueSnapshots.Add(issue);
                db.IssueTriage.Add(new IssueTriage { WorkspaceId = repo.WorkspaceId, IssueId = issue.Id, Status = status,
                    Priority = priority, AssigneeId = assignee, EnteredReviewAt = status == "needsReview" ? when ?? clock.Now : null,
                    UpdatedAt = clock.Now });
                return issue.Id;
            }
        }

        var overview = await Get("/reports/overview");
        var review = await Get("/issues?view=needsReview&pageSize=1");
        Assert.Equal(2, Total(review));
        Assert.Equal(Total(review), overview.GetProperty("needsReview").GetInt32());
        Assert.Equal(oldest, overview.GetProperty("oldestIssueId").GetGuid());
        Assert.Equal(oldest, review.GetProperty("items")[0].GetProperty("issueId").GetGuid());
        Assert.Equal(2, Total(await Get("/issues?view=needsReview&pageSize=1&page=201")));
        Assert.Empty((await Get("/issues?view=needsReview&pageSize=1&page=201")).GetProperty("items").EnumerateArray());
        Assert.Equal(1, overview.GetProperty("availableToClaim").GetInt32());
        Assert.Equal(1, Total(await Get("/issues?view=available")));
        var workload = await Get("/reports/workload");
        Assert.Equal(1, workload.GetProperty("unassignedReady").GetInt32());
        Assert.Equal(1, workload.GetProperty("byAssignee")[0].GetProperty("activeIssues").GetInt32());
        Assert.Equal(1, Total(await Get("/issues?view=activeWork&assigneeId=" + userId)));
        Assert.Equal(1, Total(await Get("/issues?view=myWork&githubState=closed")));
        Assert.Equal(2, Total(await Get("/issues?q=ClI%20%2342")));
        Assert.Equal(1, Total(await Get("/issues?q=TEAM%2FCLI%20%2342")));
        Assert.Equal(1, Total(await Get("/issues?q=%25")));
        Assert.Equal(0, Total(await Get("/issues?q=secret")));
        Assert.Equal(1, Total(await Get("/issues?status=ready&priority=high&assigneeId=unassigned&q=CRASH")));
        Assert.Equal(0, Total(await Get("/issues?status=ready&githubState=closed")));
        var sorted = (await Get("/issues?sort=priority&direction=desc")).GetProperty("items");
        Assert.Equal("low", sorted[0].GetProperty("triage").GetProperty("priority").GetString());
        Assert.Equal(JsonValueKind.Null, sorted[4].GetProperty("triage").GetProperty("priority").ValueKind);
        foreach (var bad in new[] { "q=%20", "q=%00", "q=one&q=two", "priority=urgent", "unknown=1", "sort=old", "direction=up", "assigneeId=bad" })
            await Send(owner, HttpMethod.Get, path + "/issues?" + bad, null, HttpStatusCode.BadRequest);

        await Command(owner, path + "/issues/" + assigned, "changePriority", 1, new { priority = "low" });
        var activity = await Get("/activity?repositoryId=" + mainRepository);
        Assert.Equal(2, Total(activity));
        Assert.Equal("priorityChanged", activity.GetProperty("items")[0].GetProperty("kind").GetString());
        Assert.Equal("status", activity.GetProperty("items")[1].GetProperty("changes")[0].GetProperty("field").GetString());
        Assert.DoesNotContain("Secret", activity.GetRawText());
        Assert.Equal(1, (await Get("/activity?page=2&pageSize=1")).GetProperty("items").GetArrayLength());
        foreach (var route in new[] { "/issues", "/reports/overview", "/reports/workload", "/activity" })
            await Send(outsider, HttpMethod.Get, path + route, null, HttpStatusCode.NotFound);
        Assert.Equal(1, Total(await Send(outsider, HttpMethod.Get, otherPath + "/issues", null, HttpStatusCode.OK)));

        Task<JsonElement> Get(string url) => Send(owner, HttpMethod.Get, path + url, null, HttpStatusCode.OK);
    }

    [Fact]
    public async Task FilterLatencyOnFiveThousandIssues()
    {
        var clock = new ImportClock();
        using var host = CreateReconciliationHost(new ReconciliationFixture(clock), clock);
        using var owner = await SignIn(host);
        var path = await Workspace(owner);
        var workspaceId = Guid.Parse(path.Split('/')[^1]);
        var actor = (await Send(owner, HttpMethod.Get, "/me", null, HttpStatusCode.OK)).GetProperty("userId").GetGuid();
        Guid repoId;
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TriageDeskDbContext>();
            repoId = QueueRepository(db, workspaceId, "bench/cli", clock.Now).Id;
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "IssueSnapshots"
                    ("WorkspaceId", "Id", "RepositoryId", "GitHubId", "Number", "Title", "State", "Labels",
                     "SourceAvailable", "ObservedAt", "SourceCreatedAt", "SourceUpdatedAt")
                SELECT {workspaceId}, uuidv7(), {repoId}, generated + 3000000, generated,
                    CASE WHEN generated % 7 = 0 THEN 'Synthetic profile crash ' ELSE 'Synthetic report ' END || generated,
                    CASE WHEN generated % 9 = 0 THEN 'closed' ELSE 'open' END, ARRAY[]::text[],
                    generated % 50 <> 0, now(), now(), now()
                FROM generate_series(1, 5000) AS generated
                """);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "IssueTriage"
                    ("WorkspaceId", "IssueId", "Status", "Priority", "AssigneeId", "EnteredReviewAt", "UpdatedAt", "DecisionVersion")
                SELECT "WorkspaceId", "Id",
                    CASE WHEN "Number" % 4 = 0 THEN 'ready' WHEN "Number" % 4 = 1 THEN 'inProgress'
                         WHEN "Number" % 4 = 2 THEN 'needsReview' ELSE 'done' END,
                    CASE WHEN "Number" % 2 = 0 THEN 'high' ELSE 'normal' END,
                    CASE WHEN "Number" % 4 = 1 THEN {actor} ELSE NULL END, now(), now(), 1
                FROM "IssueSnapshots" WHERE "WorkspaceId" = {workspaceId}
                """);
            await db.Database.ExecuteSqlRawAsync("""ANALYZE "IssueSnapshots"; ANALYZE "IssueTriage"; ANALYZE "Repositories";""");
        }

        string[] filters = ["view=needsReview", "view=available&priority=high&page=4",
            "q=PROFILE%20CRASH&githubState=open&sort=number&direction=desc", "q=bench%2Fcli%20%23171",
            "view=activeWork&assigneeId=" + actor, "repositoryId=" + repoId + "&status=done&sort=priority"];
        var first = new List<double>();
        var warm = new List<double>();
        foreach (var filter in filters) first.Add(await Measure(filter));
        for (var pass = 0; pass < 6; pass++)
            foreach (var filter in filters) warm.Add(await Measure(filter));
        var p95 = Percentile95(warm);
        if (Environment.GetEnvironmentVariable("TRIAGEDESK_FILTER_BENCHMARK") is { Length: > 0 } evidence)
        {
            await File.WriteAllLinesAsync(evidence, [
                "Triage Desk Slice 7 filter latency",
                "Local PostgreSQL 18 and ASP.NET Core integration host. 5,000 synthetic snapshots and triage records.",
                "Timings include the authenticated HTTP request and response deserialization.",
                "First pass is the first request for each query shape. Fixture setup populated PostgreSQL pages and ran ANALYZE.",
                "Warm pass repeats six representative filters six times. Target p95 is below 1,000 ms.",
                $"First pass (n={first.Count}) p95 {Format(Percentile95(first))} ms",
                $"Warm pass (n={warm.Count}) p95 {Format(p95)} ms",
                "First requests by filter (ms): " + string.Join(", ", first.Select(Format))
            ]);
            Assert.True(p95 < 1_000, $"Warm p95 was {Format(p95)} ms.");
        }

        async Task<double> Measure(string query)
        {
            var started = Stopwatch.GetTimestamp();
            var response = await Send(owner, HttpMethod.Get, path + "/issues?" + query, null, HttpStatusCode.OK);
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            Assert.True(Total(response) > 0);
            return elapsed;
        }
    }

    private static int Total(JsonElement page) => page.GetProperty("total").GetInt32();

    private static RepositoryConnection QueueRepository(TriageDeskDbContext db, Guid workspaceId, string name, DateTimeOffset now)
    {
        var repo = new RepositoryConnection { WorkspaceId = workspaceId, Id = Guid.CreateVersion7(), FullName = name,
            GitHubId = Random.Shared.NextInt64(100_000_000, 10_000_000_000), ConnectedAt = now };
        db.Repositories.Add(repo);
        return repo;
    }

    private static double Percentile95(List<double> durations) =>
        durations.Order().ElementAt((int)Math.Ceiling(durations.Count * 0.95) - 1);

    private static string Format(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);
}
