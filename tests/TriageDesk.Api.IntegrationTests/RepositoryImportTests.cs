using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using TriageDesk.Api.Modules.IdentityAndWorkspaces;
using TriageDesk.Api.Modules.RepositoriesAndImport;
using TriageDesk.Api.Persistence;
using Xunit;

namespace TriageDesk.Api.IntegrationTests;

public sealed partial class RepositoryImportTests
{
    [Fact]
    public async Task BatchCommitIsAtomicAndCheckpointReplayNeverRepeatsGitHubReads()
    {
        var github = new PublicGitHub();
        var fault = new FailBeforeCommit();
        var clock = new ImportClock();
        using var host = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.AddHttpClient("GitHubIssuesClient").ConfigurePrimaryHttpMessageHandler(() => github);
                services.AddSingleton<TimeProvider>(clock);
                services.ConfigureDbContext<TriageDeskDbContext>(options => options.AddInterceptors(fault));
            }));
        using var owner = await SignIn(host);
        var workspace = await Send(owner, HttpMethod.Post, "/workspaces", new { name = "Import tests" }, HttpStatusCode.Created);
        var path = $"/workspaces/{workspace.GetProperty("workspaceId").GetGuid()}";
        await Send(owner, HttpMethod.Post, path + "/repository-previews", new { fullName = "https://github.com/acme/cli" }, HttpStatusCode.BadRequest);
        Assert.Equal(0, github.Calls);

        var connected = await Send(owner, HttpMethod.Post, path + "/repositories", new { fullName = "acme/cli" }, HttpStatusCode.Created);
        var id = connected.GetProperty("repositoryId").GetGuid();
        var repo = path + "/repositories/" + id;
        var repeat = await Send(owner, HttpMethod.Post, path + "/repositories", new { fullName = "acme/cli" }, HttpStatusCode.OK);
        Assert.Equal(id, repeat.GetProperty("repositoryId").GetGuid());

        var runId = Guid.NewGuid();
        var run = repo + "/sync-runs/" + runId;
        await Send(owner, HttpMethod.Post, repo + "/sync-runs", new { runId }, HttpStatusCode.Created);
        await Send(owner, HttpMethod.Post, repo + "/sync-runs", new { runId }, HttpStatusCode.OK);
        await Send(owner, HttpMethod.Post, repo + "/sync-runs", new { runId = Guid.NewGuid() }, HttpStatusCode.Conflict);
        await Advance(1, HttpStatusCode.Conflict);

        // The interceptor fails after EF sends the page's writes, before the transaction commits.
        fault.Armed = true;
        await Advance(0, HttpStatusCode.InternalServerError);
        var rolledBack = await Send(owner, HttpMethod.Get, run, null, HttpStatusCode.OK);
        Assert.Equal(0, rolledBack.GetProperty("checkpoint").GetInt32());
        Assert.Equal(0, (await Inbox()).GetProperty("total").GetInt32());
        var freshness = await Send(owner, HttpMethod.Get, repo, null, HttpStatusCode.OK);
        Assert.Equal(JsonValueKind.Null, freshness.GetProperty("lastSuccessAt").ValueKind);

        await Advance(0, HttpStatusCode.Conflict);
        clock.Now += TimeSpan.FromSeconds(41);
        var first = await Advance(0, HttpStatusCode.OK);
        Assert.Equal(1, first.GetProperty("checkpoint").GetInt32());
        Assert.Equal(1, first.GetProperty("imported").GetInt32());
        var calls = github.Calls;
        await Advance(0, HttpStatusCode.OK);
        Assert.Equal(calls, github.Calls);

        var paused = await Send(owner, HttpMethod.Post, run + "/pause", new { expectedCheckpoint = 1 }, HttpStatusCode.OK);
        Assert.Equal("paused", paused.GetProperty("state").GetString());
        github.MalformedSecond = true;
        await Advance(1, HttpStatusCode.BadGateway);
        Assert.Equal(1, (await Inbox()).GetProperty("total").GetInt32());
        var pending = await Send(owner, HttpMethod.Get, run, null, HttpStatusCode.OK);
        Assert.Equal(1, pending.GetProperty("checkpoint").GetInt32());
        Assert.Equal("upstream_error", pending.GetProperty("lastErrorCode").GetString());

        github.MalformedSecond = false;
        var attempts = github.Calls;
        await Advance(1, HttpStatusCode.BadGateway);
        Assert.Equal(attempts, github.Calls);
        clock.Now += TimeSpan.FromSeconds(2);
        var success = await Advance(1, HttpStatusCode.OK);
        Assert.Equal("succeeded", success.GetProperty("state").GetString());
        Assert.Equal(4, success.GetProperty("examined").GetInt32());
        Assert.Equal(2, success.GetProperty("imported").GetInt32());

        var inbox = await Inbox();
        Assert.Equal(2, inbox.GetProperty("total").GetInt32());
        var summary = inbox.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("source").GetProperty("number").GetInt32() == 42);
        Assert.False(summary.GetProperty("source").TryGetProperty("body", out _));
        var issue = path + "/issues/" + summary.GetProperty("issueId").GetGuid();
        var detail = await Send(owner, HttpMethod.Get, issue, null, HttpStatusCode.OK);
        Assert.Equal("Plain source report", detail.GetProperty("source").GetProperty("body").GetString());
        Assert.Equal("needsReview", detail.GetProperty("triage").GetProperty("status").GetString());

        var other = await Send(owner, HttpMethod.Post, "/workspaces", new { name = "Other workspace" }, HttpStatusCode.Created);
        var otherPath = $"/workspaces/{other.GetProperty("workspaceId").GetGuid()}";
        await Send(owner, HttpMethod.Get, otherPath + issue[path.Length..], null, HttpStatusCode.NotFound);

        await Send(owner, HttpMethod.Delete, repo, null, HttpStatusCode.NoContent);
        Assert.Equal(0, (await Inbox()).GetProperty("total").GetInt32());
        var masked = await Send(owner, HttpMethod.Get, issue, null, HttpStatusCode.OK);
        Assert.Equal(JsonValueKind.Null, masked.GetProperty("source").ValueKind);
        Assert.Equal(1, masked.GetProperty("triage").GetProperty("decisionVersion").GetInt32());
        var reconnected = await Send(owner, HttpMethod.Post, path + "/repositories", new { fullName = "acme/cli" }, HttpStatusCode.OK);
        Assert.Equal(id, reconnected.GetProperty("repositoryId").GetGuid());
        Assert.Equal(0, (await Inbox()).GetProperty("total").GetInt32());

        Task<JsonElement> Advance(int checkpoint, HttpStatusCode status) =>
            Send(owner, HttpMethod.Post, run + "/advance", new { expectedCheckpoint = checkpoint }, status);
        Task<JsonElement> Inbox() => Send(owner, HttpMethod.Get, path + "/issues?view=needsReview", null, HttpStatusCode.OK);
    }

    private static async Task<HttpClient> SignIn(WebApplicationFactory<Program> host)
    {
        using var scope = host.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<Account>>();
        var email = Guid.NewGuid() + "@example.test";
        const string password = "local test password 2026";
        Assert.True((await users.CreateAsync(new Account
        {
            Id = Guid.CreateVersion7(), UserName = email, Email = email,
            DisplayName = "Import Owner", EmailConfirmed = true
        }, password)).Succeeded);
        var client = host.CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        await Send(client, HttpMethod.Post, "/auth/login", new { email, password }, HttpStatusCode.NoContent);
        return client;
    }

    private static async Task<JsonElement> Send(HttpClient client, HttpMethod method, string path, object? data, HttpStatusCode status)
    {
        using var request = new HttpRequestMessage(method, "/api/v1" + path);
        if (method != HttpMethod.Get)
        {
            var csrf = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/csrf");
            request.Headers.Add("X-CSRF-Token", csrf.GetProperty("requestToken").GetString());
        }
        if (data is not null) request.Content = JsonContent.Create(data);
        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"{method} {path} expected {status}, got {response.StatusCode}: {body}");
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement.Clone();
    }

    private sealed class ImportClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FailBeforeCommit : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData data, int result,
            CancellationToken cancellationToken = default)
        {
            if (Armed && data.Context!.ChangeTracker.Entries<IssueSnapshot>().Any(x => x.Entity.Title == "Demo report"))
            {
                Armed = false;
                throw new InvalidOperationException("Fixture interrupted the import before commit.");
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class PublicGitHub : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public bool MalformedSecond { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("api.github.com", request.RequestUri!.Host);
            Assert.Null(request.Headers.Authorization);
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            const string first = """
                {"id":901,"number":42,"title":"Demo report","body":"Plain source report","state":"open","labels":[{"name":"bug"}],"created_at":"2026-10-11T09:00:00Z","updated_at":"2026-10-12T09:00:00Z"}
                """;
            const string second = """
                {"id":902,"number":44,"title":"Another report","body":null,"state":"open","labels":[],"created_at":"2026-10-11T09:00:00Z","updated_at":"2026-10-12T09:00:00Z"}
                """;
            if (request.RequestUri.AbsolutePath == "/repos/acme/cli")
                response.Content = new StringContent("""{"id":801,"full_name":"acme/cli","private":false}""");
            else
            {
                Assert.Equal("/repos/acme/cli/issues", request.RequestUri.AbsolutePath);
                Assert.Contains("state=open", request.RequestUri.Query);
                if (request.RequestUri.Query.EndsWith("page=1", StringComparison.Ordinal))
                {
                    response.Content = new StringContent("[" + first + """,{"pull_request":{}}]""");
                    response.Headers.TryAddWithoutValidation("Link", """<https://api.github.com/repositories/801/issues?page=2>; rel="next" """.Trim());
                }
                else
                {
                    Assert.EndsWith("page=2", request.RequestUri.Query);
                    response.Content = new StringContent("[" + first + "," + (MalformedSecond ? "{}" : second) + "]");
                }
            }
            return Task.FromResult(response);
        }
    }
}
