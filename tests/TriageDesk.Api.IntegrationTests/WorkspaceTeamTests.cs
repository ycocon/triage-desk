using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TriageDesk.Api.Modules.IdentityAndWorkspaces;
using TriageDesk.Api.Persistence;
using Xunit;

namespace TriageDesk.Api.IntegrationTests;

public sealed class WorkspaceTeamTests
{
    private static readonly Uri Origin = new("https://localhost");
    private const string Password = "local test password 2026";

    [Fact]
    public async Task InviteChecksRecipientRevocationAndSevenDayExpiry()
    {
        var clock = new TeamClock();
        using var host = CreateHost(clock);
        using var owner = await SignIn(host);
        using var recipient = await SignIn(host);
        using var other = await SignIn(host);
        var workspace = await CreateWorkspace(owner.Client);

        var old = await Invite(owner.Client, workspace, recipient.Email, "reviewer");
        using var wrong = await Send(other.Client, HttpMethod.Post, "/invitations/preview", new { old.Token });
        Assert.Equal(HttpStatusCode.NotFound, wrong.StatusCode);
        using var absent = await Send(recipient.Client, HttpMethod.Post, "/invitations/preview", new { token = "unknown" });
        Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
        Assert.Equal((await Json(wrong)).GetProperty("code").GetString(), (await Json(absent)).GetProperty("code").GetString());

        var foreign = await CreateWorkspace(other.Client);
        using var foreignRevoke = await Send(other.Client, HttpMethod.Delete, $"/workspaces/{foreign}/invitations/{old.Id}");
        Assert.Equal(HttpStatusCode.NotFound, foreignRevoke.StatusCode);

        clock.UtcNow = old.ExpiresAt.AddSeconds(-1);
        using var preview = await Send(recipient.Client, HttpMethod.Post, "/invitations/preview", new { old.Token });
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        clock.UtcNow = old.ExpiresAt;
        using var expired = await Send(recipient.Client, HttpMethod.Post, "/invitations/accept", new { old.Token });
        Assert.Equal(HttpStatusCode.Gone, expired.StatusCode);
        Assert.Equal("invitation_expired", (await Json(expired)).GetProperty("code").GetString());

        var replacement = await Invite(owner.Client, workspace, recipient.Email.ToUpperInvariant(), "member");
        var revokeUrl = $"/workspaces/{workspace}/invitations/{replacement.Id}";
        using var revoke = await Send(owner.Client, HttpMethod.Delete, revokeUrl);
        using var replay = await Send(owner.Client, HttpMethod.Delete, revokeUrl);
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, replay.StatusCode);
        using var revoked = await Send(recipient.Client, HttpMethod.Post, "/invitations/accept", new { replacement.Token });
        Assert.Equal(HttpStatusCode.Gone, revoked.StatusCode);
        Assert.Equal("invitation_revoked", (await Json(revoked)).GetProperty("code").GetString());

        clock.UtcNow += TimeSpan.FromDays(30);
        using var aged = await Send(recipient.Client, HttpMethod.Post, "/invitations/preview", new { replacement.Token });
        Assert.Equal(HttpStatusCode.NotFound, aged.StatusCode);
        await Invite(owner.Client, workspace, recipient.Email, "member");
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TriageDeskDbContext>();
        Assert.Equal(1, await db.Invitations.CountAsync(i => i.WorkspaceId == workspace));
    }

    [Fact]
    public async Task ConcurrentAcceptanceAndReplayLeaveOneMembershipAndOneAuditEntry()
    {
        using var host = CreateHost(new TeamClock());
        using var owner = await SignIn(host);
        using var reviewer = await SignIn(host);
        var workspace = await CreateWorkspace(owner.Client);
        var invitation = await Invite(owner.Client, workspace, reviewer.Email, "reviewer");
        var createUrl = $"/workspaces/{workspace}/invitations";
        using var duplicate = await Send(owner.Client, HttpMethod.Post, createUrl, new { email = reviewer.Email.ToUpperInvariant(), role = "member" });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        using var secondSession = CreateClient(host);
        await Login(secondSession, reviewer.Email);
        var replies = await Task.WhenAll(
            Send(reviewer.Client, HttpMethod.Post, "/invitations/accept", new { invitation.Token }),
            Send(secondSession, HttpMethod.Post, "/invitations/accept", new { invitation.Token }));
        foreach (var reply in replies)
        {
            using (reply)
            {
                Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
                Assert.Equal("reviewer", (await Json(reply)).GetProperty("myRole").GetString());
            }
        }

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TriageDeskDbContext>();
        Assert.Equal(2, await db.WorkspaceMembers.CountAsync(m => m.WorkspaceId == workspace && m.RemovedAt == null));
        var row = await db.Invitations.SingleAsync(i => i.WorkspaceId == workspace && i.Id == invitation.Id);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(invitation.Token))), row.TokenDigest);
        var audit = await db.TeamAudit.Where(a => a.WorkspaceId == workspace).ToListAsync();
        Assert.Single(audit, item => item.Action == "invitation_accepted");
        var serialized = JsonSerializer.Serialize(audit);
        Assert.DoesNotContain(invitation.Token, serialized);
        Assert.DoesNotContain(row.TokenDigest, serialized);
        Assert.DoesNotContain(reviewer.Email, serialized);

        using var roleDenied = await reviewer.Client.GetAsync("/api/v1" + createUrl);
        Assert.Equal(HttpStatusCode.Forbidden, roleDenied.StatusCode);
        using var removed = await Send(owner.Client, HttpMethod.Delete, $"/workspaces/{workspace}/members/{reviewer.UserId}");
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        using var oldAgain = await Send(reviewer.Client, HttpMethod.Post, "/invitations/accept", new { invitation.Token });
        Assert.Equal(HttpStatusCode.Conflict, oldAgain.StatusCode);

        var rejoin = await Invite(owner.Client, workspace, reviewer.Email, "member");
        using var joined = await Send(reviewer.Client, HttpMethod.Post, "/invitations/accept", new { rejoin.Token });
        Assert.Equal("member", (await Json(joined)).GetProperty("myRole").GetString());
        Assert.Equal(1, await db.WorkspaceMembers.CountAsync(m => m.WorkspaceId == workspace && m.UserId == reviewer.UserId));
    }

    [Fact]
    public async Task OwnershipTransferAndRemovalSerializeWithoutLosingTheOwner()
    {
        using var host = CreateHost(new TeamClock());
        using var owner = await SignIn(host);
        using var teammate = await SignIn(host);
        using var outsider = await SignIn(host);
        var workspace = await CreateWorkspace(owner.Client);
        var separate = await CreateWorkspace(outsider.Client);
        var route = $"/workspaces/{workspace}";
        var invitation = await Invite(owner.Client, workspace, teammate.Email, "member");
        using var joined = await Send(teammate.Client, HttpMethod.Post, "/invitations/accept", new { invitation.Token });
        Assert.Equal(HttpStatusCode.OK, joined.StatusCode);
        using var cannotLeave = await Send(owner.Client, HttpMethod.Delete, route + "/membership");
        Assert.Equal(HttpStatusCode.Conflict, cannotLeave.StatusCode);
        using var wrongWorkspace = await Send(outsider.Client, HttpMethod.Delete, $"/workspaces/{separate}/members/{teammate.UserId}");
        Assert.Equal(HttpStatusCode.NotFound, wrongWorkspace.StatusCode);
        using var forbidden = await Send(teammate.Client, HttpMethod.Patch, route + $"/members/{owner.UserId}", new { role = "reviewer" });
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        using var secondOwnerSession = CreateClient(host);
        await Login(secondOwnerSession, owner.Email);
        var writes = await Task.WhenAll(
            Send(owner.Client, HttpMethod.Put, route + "/ownership", new { newOwnerId = teammate.UserId }),
            Send(secondOwnerSession, HttpMethod.Delete, route + $"/members/{teammate.UserId}"));
        using var transfer = writes[0];
        using var removal = writes[1];
        Assert.True(
            (transfer.StatusCode == HttpStatusCode.OK && removal.StatusCode == HttpStatusCode.Forbidden) ||
            (transfer.StatusCode == HttpStatusCode.NotFound && removal.StatusCode == HttpStatusCode.NoContent));

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TriageDeskDbContext>();
        var currentOwner = await db.Workspaces.Where(w => w.Id == workspace).Select(w => w.OwnerUserId).SingleAsync();
        Assert.Equal(transfer.IsSuccessStatusCode ? teammate.UserId : owner.UserId, currentOwner);
        Assert.Equal(1, await db.WorkspaceMembers.CountAsync(m =>
            m.WorkspaceId == workspace && m.UserId == currentOwner && m.RemovedAt == null));
    }

    private static WebApplicationFactory<Program> CreateHost(TeamClock clock) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.AddSingleton<TimeProvider>(clock);
            services.PostConfigure<CookieAuthenticationOptions>(IdentityConstants.ApplicationScheme,
                options => options.TimeProvider = TimeProvider.System);
        }));

    private static HttpClient CreateClient(WebApplicationFactory<Program> host) => host.CreateClient(
        new WebApplicationFactoryClientOptions { BaseAddress = Origin, AllowAutoRedirect = false });

    private static async Task<Session> SignIn(WebApplicationFactory<Program> host)
    {
        using var scope = host.Services.CreateScope();
        var accounts = scope.ServiceProvider.GetRequiredService<UserManager<Account>>();
        var email = $"{Guid.NewGuid():N}@example.test";
        var account = new Account
        {
            Id = Guid.CreateVersion7(), UserName = email, Email = email,
            DisplayName = "Teammate", EmailConfirmed = true
        };
        Assert.True((await accounts.CreateAsync(account, Password)).Succeeded);
        var client = CreateClient(host);
        await Login(client, email);
        return new Session(client, account.Id, email);
    }

    private static async Task Login(HttpClient client, string email)
    {
        using var response = await Send(client, HttpMethod.Post, "/auth/login", new { email, password = Password });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static async Task<Guid> CreateWorkspace(HttpClient client)
    {
        using var response = await Send(client, HttpMethod.Post, "/workspaces", new { name = "Test workspace" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await Json(response)).GetProperty("workspaceId").GetGuid();
    }

    private static async Task<InviteResult> Invite(HttpClient client, Guid workspaceId, string email, string role)
    {
        using var response = await Send(client, HttpMethod.Post, $"/workspaces/{workspaceId}/invitations", new { email, role });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await Json(response);
        var invitation = body.GetProperty("invitation");
        var uri = new Uri(body.GetProperty("acceptUrl").GetString()!);
        Assert.Empty(uri.Query);
        Assert.Equal("/join", uri.AbsolutePath);
        Assert.StartsWith("#token=", uri.Fragment);
        return new InviteResult(invitation.GetProperty("invitationId").GetGuid(),
            invitation.GetProperty("expiresAt").GetDateTimeOffset(), uri.Fragment["#token=".Length..]);
    }

    private static async Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string path, object? data = null)
    {
        var csrf = await client.GetFromJsonAsync<Csrf>("/api/v1/auth/csrf");
        using var request = new HttpRequestMessage(method, "/api/v1" + path);
        request.Headers.Add("X-CSRF-Token", csrf!.RequestToken);
        if (data is not null) request.Content = JsonContent.Create(data);
        return await client.SendAsync(request);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    private sealed record Session(HttpClient Client, Guid UserId, string Email) : IDisposable
    {
        public void Dispose() => Client.Dispose();
    }

    private sealed record InviteResult(Guid Id, DateTimeOffset ExpiresAt, string Token);
    private sealed record Csrf(string RequestToken);
    private sealed class TeamClock : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
